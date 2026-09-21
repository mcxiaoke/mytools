using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KeepOn.Models;
using KeepOn.Services;

namespace KeepOn.Views
{
    /// <summary>
    /// 设置窗口：编辑配置、切换工作模式，并实时反映控制器状态。
    /// 所有"应用"动作立即作用于运行中的控制器，关闭窗口不影响已生效的保持唤醒状态。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private List<string> _processes;

        /// <summary>
        /// 离屏渲染（UI 视觉验证）时由渲染脚本通过反射置为 true，跳过工作区尺寸收敛，
        /// 以便按内容完整高度截图。正常运行路径下始终为 false。
        /// 仅在反射路径赋值，故显式抑制"从未赋值"警告。
        /// </summary>
#pragma warning disable 0649
        private bool _suppressClamp;
#pragma warning restore 0649

        public SettingsWindow(App app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));

            InitializeComponent();
            LoadFromConfig();
            RefreshFromController();

            // 布局完成后再收敛尺寸：此时 SystemParameters.WorkArea 已按当前 DPI 折算为 DIP，
            // 能正确反映可用高度（高 DPI 缩放下 DIP 高度会明显小于屏幕物理高度）。
            Loaded += (s, e) => ClampToWorkArea();
        }

        /// <summary>
        /// 把窗口尺寸收敛到当前显示器工作区内，避免在高 DPI（如 150%）或小屏设备上
        /// 默认高度超出可用区域，导致底部按钮不可达。内容区由 ScrollViewer 承载，
        /// 因此缩小窗口不会丢失任何设置项。
        /// </summary>
        private void ClampToWorkArea()
        {
            if (_suppressClamp) return;

            try
            {
                Rect workArea = SystemParameters.WorkArea;

                // 留出少量边距，避免贴边或被任务栏遮挡
                double maxHeight = Math.Max(MinHeight, workArea.Height - 40);
                double maxWidth = Math.Max(MinWidth, workArea.Width - 40);

                if (Height > maxHeight) Height = maxHeight;
                if (Width > maxWidth) Width = maxWidth;

                // 手动居中到工作区
                WindowStartupLocation = WindowStartupLocation.Manual;
                Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
                Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            }
            catch (Exception ex)
            {
                Log.Error("Settings", "窗口尺寸自适应失败", ex);
            }
        }

        #region 载入与刷新

        private void LoadFromConfig()
        {
            AppConfig config = _app.Config ?? AppConfig.CreateDefault();

            _processes = config.AutoAwakeProcesses != null
                ? ProcessHelper.NormalizeList(config.AutoAwakeProcesses)
                : new List<string>();
            ProcessListBox.ItemsSource = _processes;

            KeepDisplayOnBox.IsChecked = config.Target == AwakeTarget.SystemAndDisplay;
            DisableOnBatteryBox.IsChecked = config.DisableOnBattery;
            BatteryThresholdBox.Text = config.BatteryThreshold.ToString();
            HotkeyBox.Text = config.ToggleHotkey ?? "Win+Shift+W";
            ExitDelayBox.Text = config.ProcessExitDelaySeconds.ToString();
            RestoreModeOnStartupBox.IsChecked = config.RestoreModeOnStartup;

            // 开机自启以注册表实际状态为准，避免配置文件与系统状态不一致
            RunAtStartupBox.IsChecked = StartupManager.IsEnabled();

            TimerPresetsBox.Text = string.Join(", ", config.TimerPresets);
            UntilPresetsBox.Text = string.Join(", ", config.UntilTimePresets);

            TimedMinutesBox.Text = config.DefaultDurationMinutes.ToString();

            // 注意（已知体验问题，暂不修改）：这里取预设列表首项作为「保持至」输入框的初值，
            // 因此当预设顺序为 18:00, 20:00, 22:00, 00:00 时，输入框总是预填 18:00。
            // 若希望打开设置即选中上次使用的时刻，应改用 config.ResolveUntilTime()。
            // 当前保留 FirstOrDefault 行为以维持既有交互。
            UntilTimeBox.Text = config.UntilTimePresets.FirstOrDefault() ?? ConfigDefaults.UntilTimePresets[0];

            AboutText.Text = "KeepOn " + GetVersionText() +
                             "  ·  .NET Framework 4.8\n配置文件：" + Paths.ConfigFile;

            SelectModeRadio(AwakeMode.Passive);
        }

        /// <summary>由 App 在状态变化时调用，同步单选按钮与状态徽标</summary>
        public void RefreshFromController()
        {
            AwakeController controller = _app.Controller;
            if (controller == null) return;

            SelectModeRadio(controller.Mode);

            // 若控制器处于进程联动或电池挂起，提示用户当前实际生效的状态
            KeepDisplayOnBox.IsChecked = controller.KeepDisplayOn;

            UpdateStatusBadge();
        }

        /// <summary>仅刷新倒计时文本，避免每秒重建整个界面</summary>
        public void RefreshCountdownOnly()
        {
            UpdateStatusBadge();
        }

        private void SelectModeRadio(AwakeMode mode)
        {
            switch (mode)
            {
                case AwakeMode.Indefinite:
                    RadioIndefinite.IsChecked = true;
                    break;
                case AwakeMode.Timed:
                    RadioTimed.IsChecked = true;
                    break;
                case AwakeMode.UntilTime:
                    RadioUntilTime.IsChecked = true;
                    break;
                default:
                    RadioPassive.IsChecked = true;
                    break;
            }
        }

        private void UpdateStatusBadge()
        {
            AwakeController controller = _app.Controller;
            if (controller == null) return;

            if (controller.IsBatteryPaused)
            {
                SetBadge(BadgeStyle.Warning, "已暂停（电池供电）");
            }
            else if (controller.IsProcessTriggered)
            {
                SetBadge(BadgeStyle.Success, "进程唤醒中（" + controller.ActiveProcessTrigger + "）");
            }
            else
            {
                switch (controller.Mode)
                {
                    case AwakeMode.Indefinite:
                        SetBadge(BadgeStyle.Active, "无限期保持唤醒");
                        break;
                    case AwakeMode.Timed:
                        SetBadge(BadgeStyle.Active,
                                 "倒计时中（剩余 " + StatusFormatter.FormatRemaining(controller.RemainingTime) + "）");
                        break;
                    case AwakeMode.UntilTime:
                        SetBadge(BadgeStyle.Active,
                                 "保持至 " + controller.ExpireTime.ToString("HH:mm") +
                                 "（剩余 " + StatusFormatter.FormatRemaining(controller.RemainingTime) + "）");
                        break;
                    default:
                        SetBadge(BadgeStyle.Neutral, "已关闭（跟随系统）");
                        break;
                }
            }
        }

        private void SetBadge(BadgeStyle style, string text)
        {
            StatusBadge.Background = style.Background;
            StatusBadgeText.Foreground = style.Foreground;
            StatusBadgeText.Text = text;
        }

        #endregion

        #region 进程列表操作

        private void OnAddProcessClick(object sender, RoutedEventArgs e)
        {
            string text = ProcessInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // 支持一次粘贴多个，用逗号、分号或空格分隔
            foreach (string part in TextListParser.Split(text))
            {
                AddProcess(part);
            }

            ProcessInputBox.Text = string.Empty;
        }

        private void AddProcess(string processName)
        {
            string normalized = ProcessHelper.Normalize(processName);
            if (string.IsNullOrEmpty(normalized)) return;

            if (!_processes.Any(p => ProcessHelper.IsMatch(p, normalized)))
            {
                _processes.Add(normalized);
                RefreshProcessList();
            }
        }

        private void OnDeleteProcessClick(object sender, RoutedEventArgs e)
        {
            string selected = ProcessListBox.SelectedItem as string;
            if (selected == null) return;

            _processes.Remove(selected);
            RefreshProcessList();
        }

        private void OnClearProcessesClick(object sender, RoutedEventArgs e)
        {
            if (_processes == null || _processes.Count == 0) return;

            if (MessageBox.Show(this, "确定要清空所有关联进程吗？", "确认清空",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _processes.Clear();
                RefreshProcessList();
            }
        }

        private void RefreshProcessList()
        {
            ProcessListBox.ItemsSource = null;
            ProcessListBox.ItemsSource = _processes;
        }

        private void OnBrowseExeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    Title = "选择要联动的应用程序"
                };

                if (dialog.ShowDialog(this) == true)
                {
                    string name = Path.GetFileNameWithoutExtension(dialog.FileName);
                    ProcessInputBox.Text = name;
                    AddProcess(name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings", "选择可执行文件失败", ex);
                MessageBox.Show(this, "选择文件失败：" + ex.Message, "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnPickRunningClick(object sender, RoutedEventArgs e)
        {
            try
            {
                List<ProcessHelper.RunningProcessInfo> running =
                    ProcessHelper.GetRunningWindowProcesses("keepon");

                if (running.Count == 0)
                {
                    MessageBox.Show(this, "未找到具有可见窗口的运行中应用程序。", "KeepOn",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var menu = new ContextMenu();
                foreach (var info in running)
                {
                    var item = new MenuItem { Header = info.DisplayText, Tag = info.ProcessName };
                    item.Click += (s, ev) =>
                    {
                        string name = ((MenuItem)s).Tag as string;
                        if (!string.IsNullOrEmpty(name)) AddProcess(name);
                    };
                    menu.Items.Add(item);
                }

                var button = sender as Button;
                if (button != null)
                {
                    menu.PlacementTarget = button;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings", "获取运行中进程失败", ex);
                MessageBox.Show(this, "获取运行中进程失败：" + ex.Message, "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region 底部操作

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            AppConfig config = _app.Config;
            if (config == null)
            {
                MessageBox.Show(this, "配置尚未初始化，无法保存。", "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // --- 校验并解析各输入项 ---
            int defaultMinutes;
            if (!int.TryParse(TimedMinutesBox.Text.Trim(), out defaultMinutes)
                || defaultMinutes < ConfigDefaults.MinDurationMinutes)
            {
                MessageBox.Show(this,
                    "定时保持唤醒的分钟数无效，请输入 " + ConfigDefaults.MinDurationMinutes +
                    " - " + ConfigDefaults.MaxDurationMinutes + " 之间的整数。",
                    "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                TimedMinutesBox.Focus();
                return;
            }
            defaultMinutes = ConfigDefaults.ClampDurationMinutes(defaultMinutes);

            int threshold;
            if (!int.TryParse(BatteryThresholdBox.Text.Trim(), out threshold))
            {
                threshold = ConfigDefaults.BatteryThresholdPercent;
            }
            threshold = ConfigDefaults.ClampBatteryThreshold(threshold);

            int exitDelay;
            if (!int.TryParse(ExitDelayBox.Text.Trim(), out exitDelay)
                || exitDelay < ConfigDefaults.MinProcessExitDelaySeconds)
            {
                exitDelay = ConfigDefaults.ProcessExitDelaySeconds;
            }
            exitDelay = ConfigDefaults.ClampProcessExitDelaySeconds(exitDelay);

            List<int> timerPresets = ParseIntList(TimerPresetsBox.Text);
            if (timerPresets.Count == 0)
            {
                MessageBox.Show(this, "定时预设至少要填写一个大于 0 的分钟数。", "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TimerPresetsBox.Focus();
                return;
            }

            List<string> untilPresets = ParseTimeList(UntilPresetsBox.Text);
            if (untilPresets.Count == 0)
            {
                MessageBox.Show(this, "时刻预设格式无效，请填写如 18:00, 20:00 的形式。", "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UntilPresetsBox.Focus();
                return;
            }

            string hotkey = HotkeyBox.Text.Trim();
            if (!string.IsNullOrEmpty(hotkey)
                && !HotkeyService.TryParseGesture(hotkey, out uint _, out uint _))
            {
                MessageBox.Show(this,
                    "热键格式无效。请使用「修饰键 + 按键」的形式，例如 Win+Shift+W 或 Ctrl+Alt+K。",
                    "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                HotkeyBox.Focus();
                return;
            }

            bool untilMode = RadioUntilTime.IsChecked == true;
            string untilText = AppConfig.NormalizeTimeText(UntilTimeBox.Text);
            if (untilMode && untilText == null)
            {
                MessageBox.Show(this, "目标时刻格式无效，请输入如 18:30 的形式。", "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UntilTimeBox.Focus();
                return;
            }

            // --- 写入配置 ---
            config.Target = KeepDisplayOnBox.IsChecked == true
                ? AwakeTarget.SystemAndDisplay
                : AwakeTarget.SystemOnly;
            config.DisableOnBattery = DisableOnBatteryBox.IsChecked == true;
            config.BatteryThreshold = threshold;
            config.DefaultDurationMinutes = defaultMinutes;
            config.ToggleHotkey = string.IsNullOrEmpty(hotkey) ? "Win+Shift+W" : hotkey;
            config.ProcessExitDelaySeconds = exitDelay;
            config.AutoAwakeProcesses = ProcessHelper.NormalizeList(_processes);
            config.TimerPresets = timerPresets;
            config.UntilTimePresets = untilPresets;
            config.RestoreModeOnStartup = RestoreModeOnStartupBox.IsChecked == true;

            // 开机自启：以注册表写入结果为准
            bool wantStartup = RunAtStartupBox.IsChecked == true;
            bool startupOk = StartupManager.SetEnabled(wantStartup);
            if (!startupOk)
            {
                MessageBox.Show(this,
                    "开机自启设置未能写入注册表，该选项不会生效。\n其他设置已正常保存。",
                    "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                RunAtStartupBox.IsChecked = StartupManager.IsEnabled();
            }
            config.RunAtStartup = RunAtStartupBox.IsChecked == true;

            // --- 应用模式 ---
            AwakeController controller = _app.Controller;
            if (controller != null)
            {
                controller.UpdateConfig(config);

                if (RadioPassive.IsChecked == true)
                {
                    config.Mode = AwakeMode.Passive;
                    controller.SetPassiveByUser();
                }
                else if (RadioIndefinite.IsChecked == true)
                {
                    config.Mode = AwakeMode.Indefinite;
                    controller.SetIndefinite();
                }
                else if (RadioTimed.IsChecked == true)
                {
                    config.Mode = AwakeMode.Timed;
                    controller.SetTimed(defaultMinutes);
                }
                else if (untilMode)
                {
                    config.Mode = AwakeMode.UntilTime;
                    controller.SetUntilTime(untilText);
                }
            }

            // --- 落盘并同步热键 ---
            _app.ApplyConfigFromSettings();

            MessageBox.Show(this, "设置已保存并生效。", "KeepOn",
                MessageBoxButton.OK, MessageBoxImage.Information);

            RefreshFromController();
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定要将所有设置恢复为默认值吗？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            AppConfig defaults = AppConfig.CreateDefault();

            RadioPassive.IsChecked = true;
            TimedMinutesBox.Text = defaults.DefaultDurationMinutes.ToString();
            UntilTimeBox.Text = defaults.UntilTimePresets.First();
            KeepDisplayOnBox.IsChecked = defaults.Target == AwakeTarget.SystemAndDisplay;
            DisableOnBatteryBox.IsChecked = defaults.DisableOnBattery;
            BatteryThresholdBox.Text = defaults.BatteryThreshold.ToString();
            HotkeyBox.Text = defaults.ToggleHotkey;
            ExitDelayBox.Text = defaults.ProcessExitDelaySeconds.ToString();
            RestoreModeOnStartupBox.IsChecked = defaults.RestoreModeOnStartup;
            TimerPresetsBox.Text = string.Join(", ", defaults.TimerPresets);
            UntilPresetsBox.Text = string.Join(", ", defaults.UntilTimePresets);

            _processes = new List<string>();
            RefreshProcessList();

            // 开机自启为系统级副作用，恢复默认值时不动它，仅同步显示
            RunAtStartupBox.IsChecked = StartupManager.IsEnabled();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            OpenDirectory(Paths.DataDirectory);
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            OpenDirectory(Log.LogDirectory);
        }

        #endregion

        #region 辅助

        private static List<int> ParseIntList(string raw)
        {
            return TextListParser.ParseMinutes(raw);
        }

        private static List<string> ParseTimeList(string raw)
        {
            return TextListParser.ParseTimes(raw);
        }

        private static void OpenDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error("Settings", "打开目录失败: " + path, ex);
            }
        }

        private static string GetVersionText()
        {
            try
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "1.0.0" : v.ToString(3);
            }
            catch
            {
                return "1.0.0";
            }
        }

        #endregion
    }
}
