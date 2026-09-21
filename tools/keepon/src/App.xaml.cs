using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using KeepOn.Models;
using KeepOn.Services;
using KeepOn.Views;

namespace KeepOn
{
    /// <summary>
    /// 应用主体：持有配置、控制器、托盘图标与热键服务，负责把三者接线起来。
    /// </summary>
    public partial class App : Application
    {
        private readonly CommandLineOptions _options;

        private ConfigService _configService;
        private AppConfig _config;
        private AwakeController _controller;
        private HotkeyService _hotkeys;

        private TaskbarIcon _tray;
        private SettingsWindow _settingsWindow;

        private MenuItem _rootMenuItem;
        private MenuItem _passiveItem;
        private MenuItem _indefiniteItem;
        private MenuItem _timedRootItem;
        private MenuItem _untilRootItem;
        private MenuItem _keepDisplayItem;
        private MenuItem _batteryItem;

        private DateTime _lastTrayTextUpdate = DateTime.MinValue;

        public App(CommandLineOptions options)
        {
            _options = options ?? new CommandLineOptions();

            // App.xaml 以 Resource 方式编译（不生成 InitializeComponent），
            // 共享样式在构造阶段通过代码加载。
            LoadSharedStyles();
        }

        /// <summary>加载 Styles/CommonStyles.xaml 到应用级资源，供各窗口引用 StaticResource。</summary>
        private void LoadSharedStyles()
        {
            try
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Styles/CommonStyles.xaml", UriKind.Absolute)
                };
                Resources.MergedDictionaries.Add(dictionary);
            }
            catch (Exception ex)
            {
                Log.Error("App", "加载共享样式失败", ex);
            }
        }

        /// <summary>供设置窗口访问的共享上下文</summary>
        public AppConfig Config => _config;
        public AwakeController Controller => _controller;
        public ConfigService ConfigService => _configService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                Log.Error("App", "UI 线程未处理异常", args.Exception);
                MessageBox.Show(
                    "KeepOn 遇到一个未处理的错误：\n" + args.Exception.Message +
                    "\n\n程序会继续运行，详细信息已写入日志。",
                    "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log.Error("App", "后台线程未处理异常", args.ExceptionObject as Exception);
            };

            if (_options.ShowHelp)
            {
                MessageBox.Show(CommandLineOptions.BuildHelpText(), "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            if (_options.ShowVersion)
            {
                MessageBox.Show("KeepOn " + GetVersionText(), "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                InitializeServices();
                InitializeTray();
                InitializeHotkey();
                ApplyStartupMode();
            }
            catch (Exception ex)
            {
                Log.Error("App", "初始化失败", ex);
                MessageBox.Show("KeepOn 初始化失败：\n" + ex.Message, "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        #region 初始化

        private void InitializeServices()
        {
            Log.Info("App", "KeepOn " + GetVersionText() + " 正在启动");

            _configService = new ConfigService();
            _config = _configService.Load();

            _controller = new AwakeController(Dispatcher);
            _controller.Initialize(_config);

            _controller.StateChanged += OnControllerStateChanged;
            _controller.Expired += OnControllerExpired;
            _controller.BatteryStateChanged += OnBatteryStateChanged;
            _controller.ProcessTriggered += OnProcessTriggered;
            _controller.Tick += OnControllerTick;

            _controller.Start();

            // 可选：按上次模式恢复。默认关闭，避免开机自启时意外阻止睡眠。
            if (_config.RestoreModeOnStartup && _config.Mode != AwakeMode.Passive)
            {
                RestoreSavedMode();
            }
        }

        private void RestoreSavedMode()
        {
            switch (_config.Mode)
            {
                case AwakeMode.Indefinite:
                    _controller.SetIndefinite();
                    break;
                case AwakeMode.Timed:
                    _controller.SetTimed(_config.DefaultDurationMinutes);
                    break;
                case AwakeMode.UntilTime:
                    // 还原用户上次实际选择的时刻（而不是写死的默认值或预设首项）
                    _controller.SetUntilTime(_config.ResolveUntilTime());
                    break;
                default:
                    _controller.SetPassive();
                    break;
            }
            Log.Info("App", "已按上次模式恢复: " + _config.Mode);
        }

        private void InitializeTray()
        {
            _tray = new TaskbarIcon
            {
                ToolTipText = StatusFormatter.BuildTooltip(_controller),
                IconSource = LoadTrayIcon()
            };

            _tray.TrayMouseDoubleClick += (s, args) => ShowSettingsWindow();
            _tray.ContextMenu = BuildTrayMenu();
        }

        private static BitmapSource LoadTrayIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Icon.ico", UriKind.Absolute);
                return new BitmapImage(uri);
            }
            catch (Exception ex)
            {
                Log.Error("App", "加载托盘图标失败", ex);
                return null;
            }
        }

        private void InitializeHotkey()
        {
            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += OnHotkeyPressed;

            // 热键需要窗口句柄，先建一个隐藏窗口作为消息宿主
            var messageWindow = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Opacity = 0
            };
            messageWindow.Show();
            messageWindow.Hide();

            _hotkeys.Initialize(messageWindow);

            if (!_hotkeys.Register(_config.ToggleHotkey))
            {
                Log.Warn("Hotkey", "全局热键 '" + _config.ToggleHotkey + "' 注册失败，可在设置中更换");
            }
        }

        private void ApplyStartupMode()
        {
            if (!_options.StartMinimized)
            {
                ShowSettingsWindow();
            }
            else if (!_config.HasShownFirstRunTip)
            {
                _config.HasShownFirstRunTip = true;
                _configService.Save(_config);
                ShowBalloon("KeepOn 正在后台运行",
                            "双击托盘图标可打开设置，右键可快速切换保持唤醒模式。",
                            BalloonIcon.Info);
            }
        }

        #endregion

        #region 托盘菜单

        private ContextMenu BuildTrayMenu()
        {
            var menu = new ContextMenu();

            _rootMenuItem = new MenuItem { Header = StatusFormatter.BuildHeader(_controller), IsEnabled = false };
            menu.Items.Add(_rootMenuItem);
            menu.Items.Add(new Separator());

            _passiveItem = new MenuItem { Header = "关闭（遵循系统电源计划）" };
            _passiveItem.Click += (s, e) =>
            {
                _controller.SetPassiveByUser();
                PersistMode();
            };
            menu.Items.Add(_passiveItem);

            _indefiniteItem = new MenuItem { Header = "无限期保持唤醒" };
            _indefiniteItem.Click += (s, e) =>
            {
                _controller.SetIndefinite();
                PersistMode();
                ShowBalloon("已开启无限期保持唤醒", "系统将不会自动进入睡眠。", BalloonIcon.Info);
            };
            menu.Items.Add(_indefiniteItem);

            _timedRootItem = new MenuItem { Header = "定时保持唤醒" };
            foreach (int minutes in _config.TimerPresets)
            {
                int captured = minutes;
                var item = new MenuItem { Header = StatusFormatter.FormatMinutes(captured) };
                item.Click += (s, e) =>
                {
                    _controller.SetTimed(captured);
                    PersistMode();
                    ShowBalloon("已开启保持唤醒",
                                "时长 " + StatusFormatter.FormatMinutes(captured) + "。",
                                BalloonIcon.Info);
                };
                _timedRootItem.Items.Add(item);
            }
            _timedRootItem.Items.Add(new Separator());

            var customItem = new MenuItem { Header = "自定义分钟数..." };
            customItem.Click += (s, e) => PromptCustomMinutes();
            _timedRootItem.Items.Add(customItem);
            menu.Items.Add(_timedRootItem);

            _untilRootItem = new MenuItem { Header = "保持唤醒至指定时刻" };
            foreach (string hhmm in _config.UntilTimePresets)
            {
                string captured = hhmm;

                // 注意（已知体验问题，暂不修改）：标签直接显示 "至 HH:mm"，
                // 当预设为 00:00 时实际含义是「次日零点」，但标签容易被读成「今天零点」。
                // 若需更明确，可对 00:00 这类已过去的时刻追加「（次日）」后缀。
                var item = new MenuItem { Header = "至 " + captured };
                item.Click += (s, e) =>
                {
                    if (_controller.SetUntilTime(captured))
                    {
                        PersistMode();
                        ShowBalloon("已设置保持唤醒",
                                    "将持续保持唤醒至 " + captured + "。",
                                    BalloonIcon.Info);
                    }
                };
                _untilRootItem.Items.Add(item);
            }
            _untilRootItem.Items.Add(new Separator());

            var customUntilItem = new MenuItem { Header = "自定义时刻..." };
            customUntilItem.Click += (s, e) => PromptCustomUntilTime();
            _untilRootItem.Items.Add(customUntilItem);
            menu.Items.Add(_untilRootItem);

            menu.Items.Add(new Separator());

            _keepDisplayItem = new MenuItem { Header = "保持屏幕常亮" };
            _keepDisplayItem.Click += (s, e) =>
            {
                _controller.ToggleKeepDisplayOn();
                if (_config != null)
                {
                    _config.Target = _controller.Target;
                }
                _configService.Save(_config);
            };
            menu.Items.Add(_keepDisplayItem);

            _batteryItem = new MenuItem { Header = "电池供电时自动暂停" };
            _batteryItem.Click += (s, e) =>
            {
                _config.DisableOnBattery = !_config.DisableOnBattery;
                _controller.UpdateConfig(_config);
                _configService.Save(_config);
            };
            menu.Items.Add(_batteryItem);

            menu.Items.Add(new Separator());

            var settingsItem = new MenuItem { Header = "设置..." };
            settingsItem.Click += (s, e) => ShowSettingsWindow();
            menu.Items.Add(settingsItem);

            var logItem = new MenuItem { Header = "打开日志目录" };
            logItem.Click += (s, e) => OpenInExplorer(Log.LogDirectory);
            menu.Items.Add(logItem);

            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "退出 KeepOn" };
            exitItem.Click += (s, e) => ExitApplication();
            menu.Items.Add(exitItem);

            menu.Opened += (s, e) => RefreshTrayMenuChecks();

            return menu;
        }

        /// <summary>同步菜单项的勾选状态与动态文本</summary>
        private void RefreshTrayMenuChecks()
        {
            if (_rootMenuItem != null)
            {
                _rootMenuItem.Header = StatusFormatter.BuildHeader(_controller);
            }

            AwakeMode mode = _controller.Mode;

            if (_passiveItem != null) _passiveItem.IsChecked = mode == AwakeMode.Passive;
            if (_indefiniteItem != null) _indefiniteItem.IsChecked = mode == AwakeMode.Indefinite;
            if (_timedRootItem != null) _timedRootItem.IsChecked = mode == AwakeMode.Timed;
            if (_untilRootItem != null) _untilRootItem.IsChecked = mode == AwakeMode.UntilTime;

            if (_keepDisplayItem != null) _keepDisplayItem.IsChecked = _controller.KeepDisplayOn;
            if (_batteryItem != null) _batteryItem.IsChecked = _config != null && _config.DisableOnBattery;
        }

        private void PromptCustomMinutes()
        {
            var dialog = new InputDialog("自定义保持唤醒时长",
                                         "请输入分钟数（" + ConfigDefaults.MinDurationMinutes +
                                         " - " + ConfigDefaults.MaxDurationMinutes + "）：",
                                         _config.DefaultDurationMinutes.ToString())
            {
                Owner = _settingsWindow != null && _settingsWindow.IsVisible ? _settingsWindow : null
            };

            if (dialog.ShowDialog() != true) return;

            int minutes;
            if (!int.TryParse(dialog.InputText.Trim(), out minutes)
                || minutes < ConfigDefaults.MinDurationMinutes)
            {
                MessageBox.Show("请输入 " + ConfigDefaults.MinDurationMinutes + " - " +
                                ConfigDefaults.MaxDurationMinutes + " 之间的整数分钟数。",
                    "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            minutes = ConfigDefaults.ClampDurationMinutes(minutes);

            _controller.SetTimed(minutes);
            PersistMode();
            ShowBalloon("已开启保持唤醒", "时长 " + StatusFormatter.FormatMinutes(minutes) + "。", BalloonIcon.Info);
        }

        private void PromptCustomUntilTime()
        {
            string defaultValue = DateTime.Now.AddHours(1).ToString("HH:mm");
            var dialog = new InputDialog("保持唤醒至指定时刻", "请输入目标时刻（HH:mm，如 18:30）：", defaultValue)
            {
                Owner = _settingsWindow != null && _settingsWindow.IsVisible ? _settingsWindow : null
            };

            if (dialog.ShowDialog() != true) return;

            string normalized = AppConfig.NormalizeTimeText(dialog.InputText);
            if (normalized == null)
            {
                MessageBox.Show("时刻格式无效，请输入如 18:30 的形式。", "KeepOn",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _controller.SetUntilTime(normalized);
            PersistMode();
            ShowBalloon("已设置保持唤醒", "将持续保持唤醒至 " + normalized + "。", BalloonIcon.Info);
        }

        #endregion

        #region 控制器事件

        private void OnControllerStateChanged()
        {
            UpdateTrayVisuals();

            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.RefreshFromController();
            }
        }

        private void OnControllerTick()
        {
            // 倒计时模式下每秒刷新一次；其余情况每 10 秒刷新一次即可，避免无谓的托盘重绘
            bool counting = _controller.Mode == AwakeMode.Timed || _controller.Mode == AwakeMode.UntilTime;

            if (counting || (DateTime.Now - _lastTrayTextUpdate).TotalSeconds >= 10)
            {
                UpdateTrayVisuals();
            }

            if (counting && _settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.RefreshCountdownOnly();
            }
        }

        private void OnControllerExpired()
        {
            PersistMode();
            ShowBalloon("保持唤醒已结束", "已恢复系统常规电源策略。", BalloonIcon.Info);
        }

        private void OnBatteryStateChanged(bool isPaused, int percent)
        {
            if (isPaused)
            {
                ShowBalloon("已暂停保持唤醒",
                            "检测到电池供电或电量偏低（" + (percent >= 0 ? percent + "%" : "未知") +
                            "），为保护电池已临时挂起。",
                            BalloonIcon.Warning);
            }
            else
            {
                ShowBalloon("已恢复保持唤醒", "电源条件已恢复，继续保持唤醒。", BalloonIcon.Info);
            }
        }

        private void OnProcessTriggered(bool isTriggered, string processName)
        {
            if (isTriggered)
            {
                ShowBalloon("已自动开启保持唤醒",
                            "检测到目标进程 '" + processName + "' 正在运行。",
                            BalloonIcon.Info);
            }
            else
            {
                ShowBalloon("已自动关闭保持唤醒",
                            "目标进程 '" + processName + "' 已退出。",
                            BalloonIcon.Info);
            }
        }

        private void OnHotkeyPressed()
        {
            AwakeMode result = _controller.QuickToggle();
            PersistMode();

            string message = result == AwakeMode.Passive
                ? "已关闭保持唤醒，恢复系统默认电源策略。"
                : "已开启保持唤醒（" + StatusFormatter.BuildShortStatus(_controller) + "）。";

            ShowBalloon("KeepOn", message, BalloonIcon.Info);
        }

        #endregion

        #region 托盘视觉与辅助

        private void UpdateTrayVisuals()
        {
            _lastTrayTextUpdate = DateTime.Now;

            try
            {
                if (_tray != null)
                {
                    _tray.ToolTipText = StatusFormatter.BuildTooltip(_controller);
                }

                if (_rootMenuItem != null)
                {
                    _rootMenuItem.Header = StatusFormatter.BuildHeader(_controller);
                }
            }
            catch (Exception ex)
            {
                Log.Error("App", "更新托盘显示失败", ex);
            }
        }

        private void ShowBalloon(string title, string message, BalloonIcon icon)
        {
            try
            {
                _tray?.ShowBalloonTip(title, message, icon);
            }
            catch (Exception ex)
            {
                Log.Error("App", "显示气泡提示失败", ex);
            }
        }

        private void PersistMode()
        {
            try
            {
                if (_config == null) return;

                _config.Mode = _controller.Mode;
                _config.Target = _controller.Target;

                // 记录 UntilTime 模式下的实际目标时刻，供下次启动恢复。
                // 仅在处于该模式时更新，避免切到其他模式后把有效值覆盖掉。
                if (_controller.Mode == AwakeMode.UntilTime && _controller.ExpireTime > DateTime.MinValue)
                {
                    _config.LastUntilTime = _controller.ExpireTime.ToString("HH:mm");
                }

                _configService.Save(_config);
            }
            catch (Exception ex)
            {
                Log.Error("App", "保存模式状态失败", ex);
            }

            UpdateTrayVisuals();
        }

        #endregion

        #region 窗口

        public void ShowSettingsWindow()
        {
            try
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow(this);
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                    _settingsWindow.Show();
                }
                else
                {
                    if (_settingsWindow.WindowState == WindowState.Minimized)
                    {
                        _settingsWindow.WindowState = WindowState.Normal;
                    }
                    _settingsWindow.Show();
                    _settingsWindow.Activate();
                }

                _settingsWindow.RefreshFromController();
            }
            catch (Exception ex)
            {
                Log.Error("App", "打开设置窗口失败", ex);
            }
        }

        /// <summary>设置窗口保存后调用：落盘配置并同步热键</summary>
        public void ApplyConfigFromSettings()
        {
            if (_config == null) return;

            _controller.UpdateConfig(_config);
            _configService.Save(_config);

            // 热键可能被修改，重新注册
            if (_hotkeys != null)
            {
                if (!_hotkeys.Register(_config.ToggleHotkey))
                {
                    MessageBox.Show(
                        "全局热键 '" + _config.ToggleHotkey + "' 注册失败。\n" +
                        "该组合可能已被其他程序占用，请换一个组合后重试。",
                        "KeepOn", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            UpdateTrayVisuals();
        }

        private void ExitApplication()
        {
            try
            {
                Log.Info("App", "用户请求退出，正在清理资源");

                _controller?.Stop();
                _hotkeys?.Dispose();

                if (_tray != null)
                {
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("App", "退出清理时发生异常", ex);
            }
            finally
            {
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _controller?.Dispose();
                _hotkeys?.Dispose();
                _tray?.Dispose();
            }
            catch
            {
                // 退出阶段忽略异常
            }

            Log.Info("App", "KeepOn 已退出");
            base.OnExit(e);
        }

        private static void OpenInExplorer(string path)
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
                Log.Error("App", "打开目录失败: " + path, ex);
            }
        }

        private static string GetVersionText()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
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
