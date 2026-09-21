using System;
using System.Windows.Threading;
using KeepOn.Models;

namespace KeepOn.Services
{
    /// <summary>
    /// 保持唤醒的核心状态机，移植并重构自 CarroDesk 的 AwakeService。
    ///
    /// 职责：
    /// - 维护当前模式（Passive / Indefinite / Timed / UntilTime）与到期时间
    /// - 通过 SetThreadExecutionState 应用/清除电源请求
    /// - 定时器驱动：倒计时到期、电池策略轮询、进程联动轮询
    /// - 电池供电或低电量时自动挂起（保护笔记本电池）
    ///
    /// 所有状态变更都在 UI 线程（Dispatcher）上完成，事件回调可直接更新界面。
    /// </summary>
    public class AwakeController : IDisposable
    {
        /// <summary>定时器心跳周期：每秒一次，驱动倒计时与轮询节拍</summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        /// <summary>电池状态检查间隔（按心跳计次，即每 3 秒）</summary>
        private const int BatteryCheckIntervalTicks = 3;

        /// <summary>进程联动检查间隔（按心跳计次，即每 5 秒）</summary>
        private const int ProcessCheckIntervalTicks = 5;

        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;

        private AppConfig _config;

        private AwakeMode _mode = AwakeMode.Passive;
        private AwakeTarget _target = AwakeTarget.SystemAndDisplay;
        private DateTime _expireTime = DateTime.MinValue;

        private bool _isBatteryPaused;
        private bool _isProcessTriggered;
        private string _activeProcessTrigger = string.Empty;
        private int _processExitPendingSeconds;
        private int _tickCount;

        /// <summary>
        /// 用户在守护进程运行期间手动关闭后置位：本次进程会话内不再自动联动，
        /// 直到名单中所有进程退出才解除。用于尊重用户的显式关闭意图。
        /// </summary>
        private bool _userSuppressedProcessLink;

        #region 对外只读状态

        public AwakeMode Mode => _mode;

        public AwakeTarget Target => _target;

        /// <summary>是否保持显示器常亮</summary>
        public bool KeepDisplayOn => _target == AwakeTarget.SystemAndDisplay;

        public DateTime ExpireTime => _expireTime;

        /// <summary>是否处于有效的保持唤醒状态（排除电池挂起）</summary>
        public bool IsActive => _mode != AwakeMode.Passive && !_isBatteryPaused;

        public bool IsBatteryPaused => _isBatteryPaused;

        public bool IsProcessTriggered => _isProcessTriggered;

        public string ActiveProcessTrigger => _activeProcessTrigger;

        /// <summary>当前是否因用户手动关闭而临时禁用了进程联动</summary>
        public bool IsProcessLinkSuppressed => _userSuppressedProcessLink;

        /// <summary>定时/至指定时刻模式下的剩余时间；其他模式返回 Zero</summary>
        public TimeSpan RemainingTime
        {
            get
            {
                if ((_mode == AwakeMode.Timed || _mode == AwakeMode.UntilTime)
                    && _expireTime > DateTime.Now)
                {
                    return _expireTime - DateTime.Now;
                }
                return TimeSpan.Zero;
            }
        }

        #endregion

        #region 事件

        /// <summary>模式、目标或挂起状态发生变化</summary>
        public event Action StateChanged;

        /// <summary>定时到期，已自动恢复系统默认策略</summary>
        public event Action Expired;

        /// <summary>电池挂起状态变化：(是否已挂起, 电量百分比)</summary>
        public event Action<bool, int> BatteryStateChanged;

        /// <summary>进程联动状态变化：(是否已触发, 进程名)</summary>
        public event Action<bool, string> ProcessTriggered;

        /// <summary>每秒一次的心跳，用于界面倒计时刷新</summary>
        public event Action Tick;

        #endregion

        public AwakeController(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TickInterval
            };
            _timer.Tick += OnTimerTick;
        }

        #region 生命周期

        public void Initialize(AppConfig config)
        {
            _config = config ?? AppConfig.CreateDefault();
            _target = _config.Target;

            // 配置里记录的 mode 由外部（App）在启动时决定是否恢复，
            // 这里只同步显示相关设置，实际状态从 Passive 开始。
            _mode = AwakeMode.Passive;
            _expireTime = DateTime.MinValue;
        }

        public void Start()
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
            Log.Info("Awake", "保持唤醒控制器已启动");
        }

        public void Stop()
        {
            _timer.Stop();
            SetPassiveInternal();
            Log.Info("Awake", "保持唤醒控制器已停止，已恢复系统默认电源策略");
        }

        public void Dispose()
        {
            try
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
            }
            catch
            {
                // 释放阶段忽略异常
            }
        }

        #endregion

        #region 配置更新

        /// <summary>
        /// 应用最新配置：更新目标、电池策略与进程名单，并立即重新评估当前状态。
        /// </summary>
        public void UpdateConfig(AppConfig config)
        {
            if (config == null) return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => UpdateConfig(config)));
                return;
            }

            _config = config;
            _target = config.Target;

            ApplyExecutionState();

            // 配置变化后立即按新阈值重新判定电池状态，避免最长 3 秒的空窗
            if (_config.DisableOnBattery)
            {
                CheckBatteryStatus();
            }
            else if (_isBatteryPaused)
            {
                _isBatteryPaused = false;
                ApplyExecutionState();
            }

            StateChanged?.Invoke();
        }

        #endregion

        #region 模式切换

        /// <summary>关闭保持唤醒（内部调用，不改变用户抑制标记）</summary>
        public void SetPassive()
        {
            SetPassiveInternal();
        }

        /// <summary>
        /// 用户主动关闭（托盘菜单/设置界面）。
        ///
        /// 与内部 SetPassive 的区别：若进程联动名单中有进程正在运行，
        /// 记下"本次会话内不再自动联动"，否则下一次轮询会立刻把用户的关闭动作反转回 Indefinite。
        /// </summary>
        public void SetPassiveByUser()
        {
            _userSuppressedProcessLink = true;
            SetPassiveInternal();
            Log.Info("Awake", "用户手动关闭保持唤醒，已恢复系统默认电源策略");
        }

        public void SetIndefinite()
        {
            _mode = AwakeMode.Indefinite;
            _expireTime = DateTime.MinValue;
            ClearProcessTriggerState();

            ApplyExecutionState();
            StateChanged?.Invoke();
            Log.Info("Awake", "已切换到无限期保持唤醒");
        }

        /// <summary>定时保持唤醒；minutes <= 0 视为关闭</summary>
        public void SetTimed(int minutes)
        {
            if (minutes <= 0)
            {
                SetPassiveInternal();
                return;
            }

            _mode = AwakeMode.Timed;
            _expireTime = DateTime.Now.AddMinutes(minutes);
            ClearProcessTriggerState();

            ApplyExecutionState();
            StateChanged?.Invoke();
            Log.Info("Awake", "已切换到定时保持唤醒，时长 " + minutes + " 分钟");
        }

        /// <summary>保持唤醒至指定时刻（仅取 HH:mm，若已过则顺延到明天）</summary>
        public void SetUntilTime(DateTime targetTime)
        {
            DateTime now = DateTime.Now;
            DateTime target = new DateTime(now.Year, now.Month, now.Day,
                                           targetTime.Hour, targetTime.Minute, 0);

            // 注意：使用 <= 而非 <，因此恰好在目标时刻整点（秒/毫秒为 0）调用时，
            // 会顺延到次日同一时刻，即保持满 24 小时。这是该语义下的合理结果。
            if (target <= now)
            {
                target = target.AddDays(1);
            }

            _mode = AwakeMode.UntilTime;
            _expireTime = target;
            ClearProcessTriggerState();

            ApplyExecutionState();
            StateChanged?.Invoke();
            Log.Info("Awake", "已切换到保持唤醒至 " + target.ToString("yyyy-MM-dd HH:mm"));
        }

        /// <summary>保持唤醒至指定时刻（HH:mm 文本）</summary>
        public bool SetUntilTime(string hhmm)
        {
            string normalized = AppConfig.NormalizeTimeText(hhmm);
            if (normalized == null) return false;

            TimeSpan ts = TimeSpan.Parse(normalized);
            SetUntilTime(DateTime.Today.Add(ts));
            return true;
        }

        /// <summary>在"仅系统"与"系统+显示器"之间切换</summary>
        public void ToggleKeepDisplayOn()
        {
            SetKeepDisplayOn(!KeepDisplayOn);
        }

        /// <summary>设置是否保持显示器常亮</summary>
        public void SetKeepDisplayOn(bool keepDisplayOn)
        {
            AwakeTarget newTarget = keepDisplayOn
                ? AwakeTarget.SystemAndDisplay
                : AwakeTarget.SystemOnly;

            if (_target == newTarget) return;

            _target = newTarget;
            if (_config != null)
            {
                _config.Target = newTarget;
            }

            ApplyExecutionState();
            StateChanged?.Invoke();
            Log.Info("Awake", "电源请求目标已切换为: " + (keepDisplayOn ? "系统 + 显示器" : "仅系统"));
        }

        /// <summary>
        /// 快速切换：关闭状态下按默认时长开启（默认时长为 0 则无限期），
        /// 已开启则关闭。对齐 PowerToys Awake 的热键行为。
        /// </summary>
        public AwakeMode QuickToggle()
        {
            if (_mode == AwakeMode.Passive)
            {
                int defaultMinutes = _config != null ? _config.DefaultDurationMinutes : 30;
                if (defaultMinutes > 0)
                {
                    SetTimed(defaultMinutes);
                }
                else
                {
                    SetIndefinite();
                }
            }
            else
            {
                SetPassiveByUser();
            }

            return _mode;
        }

        #endregion

        #region 电源请求

        /// <summary>
        /// 把当前状态映射为 Windows 电源请求。
        /// Passive 或电池挂起时提交纯 ES_CONTINUOUS，等效于释放请求。
        /// </summary>
        private void ApplyExecutionState()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(ApplyExecutionState));
                return;
            }

            bool shouldHold = _mode != AwakeMode.Passive && !_isBatteryPaused;

            AwakeTargetKind? kind = null;
            if (shouldHold)
            {
                kind = _target == AwakeTarget.SystemAndDisplay
                    ? AwakeTargetKind.SystemAndDisplay
                    : AwakeTargetKind.SystemOnly;
            }

            bool ok = PowerNative.Apply(kind);

            if (!ok)
            {
                Log.Warn("Awake", "SetThreadExecutionState 调用返回失败，电源状态可能未生效");
                return;
            }

            if (shouldHold)
            {
                Log.Info("Awake", "已应用保持唤醒: " +
                    (_target == AwakeTarget.SystemAndDisplay ? "系统 + 显示器" : "仅系统") +
                    (_isProcessTriggered ? "（进程联动: " + _activeProcessTrigger + "）" : string.Empty));
            }
            else
            {
                Log.Info("Awake", "已释放保持唤醒请求 (ES_CONTINUOUS)" +
                    (_isBatteryPaused ? "（电池挂起中）" : string.Empty));
            }
        }

        #endregion

        #region 定时器驱动

        private void OnTimerTick(object sender, EventArgs e)
        {
            _tickCount++;

            // 1) 定时到期检查
            if (_mode == AwakeMode.Timed || _mode == AwakeMode.UntilTime)
            {
                if (DateTime.Now >= _expireTime)
                {
                    Log.Info("Awake", "保持唤醒已到期，恢复系统默认电源策略");
                    SetPassiveInternal();
                    Expired?.Invoke();
                    return;
                }
            }

            // 2) 进程退出缓冲倒计时
            if (_processExitPendingSeconds > 0)
            {
                _processExitPendingSeconds--;
                if (_processExitPendingSeconds <= 0)
                {
                    Log.Info("Awake", "目标进程退出缓冲期已结束，恢复系统默认电源策略");

                    if (_isProcessTriggered && _mode == AwakeMode.Indefinite)
                    {
                        string oldProcess = _activeProcessTrigger;
                        ClearProcessTriggerState();
                        SetPassiveInternal();
                        ProcessTriggered?.Invoke(false, oldProcess);
                    }
                }
            }

            // 3) 电池状态轮询
            if (_config != null && _config.DisableOnBattery && _tickCount % BatteryCheckIntervalTicks == 0)
            {
                CheckBatteryStatus();
            }

            // 4) 进程联动轮询
            if (_config != null
                && _config.AutoAwakeProcesses != null
                && _config.AutoAwakeProcesses.Count > 0
                && _tickCount % ProcessCheckIntervalTicks == 0)
            {
                CheckProcessTriggers();
            }

            // 5) 心跳，供界面刷新倒计时
            Tick?.Invoke();
        }

        private void CheckBatteryStatus()
        {
            try
            {
                PowerSnapshot? snapshot = PowerNative.TryGetPowerSnapshot();

                // 无电池（台式机）或读取失败：不干预当前状态
                if (snapshot == null || !snapshot.Value.HasBattery) return;

                int threshold = _config != null ? _config.BatteryThreshold : ConfigDefaults.BatteryThresholdPercent;
                bool shouldPause = snapshot.Value.ShouldSuspend(threshold);

                if (shouldPause && !_isBatteryPaused && _mode != AwakeMode.Passive)
                {
                    _isBatteryPaused = true;
                    ApplyExecutionState();
                    BatteryStateChanged?.Invoke(true, snapshot.Value.BatteryPercent);
                    StateChanged?.Invoke();
                    Log.Info("Awake", "检测到电池供电或低电量，已挂起保持唤醒（电量 " +
                                      snapshot.Value.BatteryPercent + "%）");
                }
                else if (!shouldPause && _isBatteryPaused)
                {
                    _isBatteryPaused = false;
                    ApplyExecutionState();
                    BatteryStateChanged?.Invoke(false, snapshot.Value.BatteryPercent);
                    StateChanged?.Invoke();
                    Log.Info("Awake", "已恢复交流供电且电量充足，保持唤醒已恢复");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Awake", "检查电池状态时发生异常", ex);
            }
        }

        private void CheckProcessTriggers()
        {
            try
            {
                string matched = ProcessHelper.FindFirstRunning(_config.AutoAwakeProcesses);

                if (!string.IsNullOrEmpty(matched))
                {
                    if (_processExitPendingSeconds > 0)
                    {
                        // 退出缓冲期内进程重新启动：取消倒计时，继续保持唤醒
                        Log.Info("Awake", "目标进程 '" + matched + "' 在缓冲期内重新启动，已取消退出倒计时");
                        _processExitPendingSeconds = 0;
                        _activeProcessTrigger = matched;
                        return;
                    }

                    if (_mode == AwakeMode.Passive)
                    {
                        if (_userSuppressedProcessLink)
                        {
                            // 用户已手动关闭，本次会话内不再自动开启
                            return;
                        }

                        _isProcessTriggered = true;
                        _activeProcessTrigger = matched;
                        _mode = AwakeMode.Indefinite;
                        _expireTime = DateTime.MinValue;

                        ApplyExecutionState();
                        ProcessTriggered?.Invoke(true, matched);
                        StateChanged?.Invoke();
                        Log.Info("Awake", "检测到目标进程 '" + matched + "' 运行，已自动开启保持唤醒");
                    }
                }
                else
                {
                    // 名单中所有进程均已退出：解除用户抑制，下次启动可重新自动联动
                    if (_userSuppressedProcessLink)
                    {
                        _userSuppressedProcessLink = false;
                    }

                    if (_isProcessTriggered && _mode == AwakeMode.Indefinite)
                    {
                        int delaySeconds = _config != null
                            ? ConfigDefaults.ClampProcessExitDelaySeconds(_config.ProcessExitDelaySeconds)
                            : ConfigDefaults.ProcessExitDelaySeconds;

                        if (delaySeconds <= 0)
                        {
                            string oldProcess = _activeProcessTrigger;
                            ClearProcessTriggerState();
                            SetPassiveInternal();
                            ProcessTriggered?.Invoke(false, oldProcess);
                            Log.Info("Awake", "目标进程已退出，立即恢复系统默认电源策略");
                        }
                        else if (_processExitPendingSeconds <= 0)
                        {
                            _processExitPendingSeconds = delaySeconds;
                            Log.Info("Awake", "目标进程已全部退出，进入退出缓冲倒计时 (" + delaySeconds + " 秒)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Awake", "检查进程联动时发生异常", ex);
            }
        }

        #endregion

        #region 内部辅助

        private void SetPassiveInternal()
        {
            _mode = AwakeMode.Passive;
            _expireTime = DateTime.MinValue;
            ClearProcessTriggerState();

            // 注意：此处刻意不重置 _isBatteryPaused——该标志归电池判定逻辑所有。
            // 若在此清零，下一次 3 秒轮询会立刻重新置起，造成状态抖动。

            ApplyExecutionState();
            StateChanged?.Invoke();
        }

        private void ClearProcessTriggerState()
        {
            _isProcessTriggered = false;
            _activeProcessTrigger = string.Empty;
            _processExitPendingSeconds = 0;
        }

        #endregion
    }
}
