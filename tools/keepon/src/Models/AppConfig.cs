using System;
using System.Collections.Generic;
using System.Linq;

namespace KeepOn.Models
{
    /// <summary>
    /// KeepOn 的持久化配置。字段命名与默认值对齐 PowerToys Awake 的可调项，
    /// 并保留 CarroDesk Awake 模块中的进程联动、电池保护等增强能力。
    ///
    /// 所有默认值与取值边界统一来自 <see cref="ConfigDefaults"/>，
    /// 本类不重复书写字面量。
    /// </summary>
    public class AppConfig
    {
        /// <summary>配置结构版本，便于后续迁移</summary>
        public int ConfigVersion { get; set; } = ConfigDefaults.ConfigVersion;

        /// <summary>启动时恢复上次的保持唤醒模式</summary>
        public bool RestoreModeOnStartup { get; set; }

        /// <summary>上次使用的模式</summary>
        public AwakeMode Mode { get; set; } = AwakeMode.Passive;

        /// <summary>电源请求目标：仅系统 / 系统+显示器</summary>
        public AwakeTarget Target { get; set; } = AwakeTarget.SystemAndDisplay;

        /// <summary>定时保持唤醒的默认时长（分钟），用于托盘快速切换</summary>
        public int DefaultDurationMinutes { get; set; } = ConfigDefaults.DurationMinutes;

        /// <summary>
        /// 上次使用「保持至指定时刻」模式时的目标时刻（HH:mm）。
        ///
        /// 用于「启动时恢复上次模式」：恢复 UntilTime 模式时应还原用户实际选择的时刻，
        /// 而不是写死的默认值或预设列表首项。为空时回退到 <see cref="ConfigDefaults.UntilTimePresets"/> 首项。
        /// </summary>
        public string LastUntilTime { get; set; }

        /// <summary>全局快速切换热键（开启/关闭切换）</summary>
        public string ToggleHotkey { get; set; } = ConfigDefaults.ToggleHotkey;

        /// <summary>使用电池供电时自动挂起保持唤醒</summary>
        public bool DisableOnBattery { get; set; } = true;

        /// <summary>电池电量低于该百分比时自动挂起</summary>
        public int BatteryThreshold { get; set; } = ConfigDefaults.BatteryThresholdPercent;

        /// <summary>托盘定时菜单中的预设时长（分钟）</summary>
        public List<int> TimerPresets { get; set; } = CreateDefaultTimerPresets();

        /// <summary>托盘"保持至指定时刻"菜单中的预设时刻（HH:mm）</summary>
        public List<string> UntilTimePresets { get; set; } = CreateDefaultUntilTimePresets();

        /// <summary>检测到以下进程运行时自动保持唤醒（进程名，含或不含 .exe 均可）</summary>
        public List<string> AutoAwakeProcesses { get; set; } = new List<string>();

        /// <summary>目标进程全部退出后延迟恢复的缓冲秒数（0 表示立即恢复）</summary>
        public int ProcessExitDelaySeconds { get; set; } = ConfigDefaults.ProcessExitDelaySeconds;

        /// <summary>开机自启（写入当前用户 Run 注册表项）</summary>
        public bool RunAtStartup { get; set; }

        /// <summary>首次启动时是否已显示过托盘气泡提示</summary>
        public bool HasShownFirstRunTip { get; set; }

        public static AppConfig CreateDefault()
        {
            return new AppConfig();
        }

        /// <summary>基于默认预设创建一份可变副本（集合属性需要可变实例以支持界面编辑）</summary>
        public static List<int> CreateDefaultTimerPresets()
        {
            return ConfigDefaults.TimerPresets.ToList();
        }

        /// <summary>基于默认预设创建一份可变副本</summary>
        public static List<string> CreateDefaultUntilTimePresets()
        {
            return ConfigDefaults.UntilTimePresets.ToList();
        }

        /// <summary>
        /// 将配置中的集合字段做防御性清洗与边界收敛，避免手工编辑配置文件后出现异常值。
        /// </summary>
        public void Sanitize()
        {
            DefaultDurationMinutes = ConfigDefaults.ClampDurationMinutes(DefaultDurationMinutes);
            BatteryThreshold = ConfigDefaults.ClampBatteryThreshold(BatteryThreshold);
            ProcessExitDelaySeconds = ConfigDefaults.ClampProcessExitDelaySeconds(ProcessExitDelaySeconds);

            TimerPresets = SanitizeTimerPresets(TimerPresets);
            UntilTimePresets = SanitizeUntilTimePresets(UntilTimePresets);

            AutoAwakeProcesses = AutoAwakeProcesses == null
                ? new List<string>()
                : Services.ProcessHelper.NormalizeList(AutoAwakeProcesses);

            if (string.IsNullOrWhiteSpace(ToggleHotkey))
            {
                ToggleHotkey = ConfigDefaults.ToggleHotkey;
            }

            // 上次的目标时刻：非法值一律清空，由使用方回退到预设首项，
            // 避免脏数据被当成有效时刻恢复。
            if (!string.IsNullOrWhiteSpace(LastUntilTime))
            {
                LastUntilTime = NormalizeTimeText(LastUntilTime);
            }

            if (!IsValidTarget(Target))
            {
                Target = AwakeTarget.SystemAndDisplay;
            }
        }

        /// <summary>清洗定时预设：丢弃非法值、去重、升序；为空则回退到默认预设</summary>
        private static List<int> SanitizeTimerPresets(List<int> presets)
        {
            if (presets == null || presets.Count == 0)
            {
                return CreateDefaultTimerPresets();
            }

            List<int> cleaned = presets
                .Where(ConfigDefaults.IsValidDurationMinutes)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            return cleaned.Count == 0 ? CreateDefaultTimerPresets() : cleaned;
        }

        /// <summary>清洗时刻预设：归一化格式、丢弃非法值、去重；为空则回退到默认预设</summary>
        private static List<string> SanitizeUntilTimePresets(List<string> presets)
        {
            if (presets == null || presets.Count == 0)
            {
                return CreateDefaultUntilTimePresets();
            }

            List<string> cleaned = presets
                .Select(NormalizeTimeText)
                .Where(t => t != null)
                .Distinct()
                .ToList();

            return cleaned.Count == 0 ? CreateDefaultUntilTimePresets() : cleaned;
        }

        /// <summary>
        /// 解析「保持至指定时刻」应使用的目标时刻：
        /// 优先使用上次实际选择的时刻，缺失或非法时回退到预设首项。
        /// </summary>
        public string ResolveUntilTime()
        {
            string last = NormalizeTimeText(LastUntilTime);
            if (last != null) return last;

            return UntilTimePresets != null && UntilTimePresets.Count > 0
                ? UntilTimePresets[0]
                : ConfigDefaults.UntilTimePresets[0];
        }

        private static bool IsValidTarget(AwakeTarget target)
        {
            return target == AwakeTarget.SystemOnly || target == AwakeTarget.SystemAndDisplay;
        }

        /// <summary>把 "8:5"、"18:00" 之类的输入规范化为 HH:mm，非法输入返回 null</summary>
        public static string NormalizeTimeText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!TimeSpan.TryParse(raw.Trim(), out TimeSpan ts)) return null;
            if (ts < TimeSpan.Zero || ts.TotalMinutes >= ConfigDefaults.MinutesPerDay) return null;

            return string.Format("{0:D2}:{1:D2}", ts.Hours, ts.Minutes);
        }
    }
}
