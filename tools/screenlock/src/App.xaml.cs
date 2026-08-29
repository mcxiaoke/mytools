using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenLock.Services;
using ScreenLock.Services.Tasks;
using ScreenLock.Views;

namespace ScreenLock
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\ScreenLock_SingleInstance_2C7A4F10";

        public static ConfigService Config { get; private set; }
        public static LockController Controller { get; private set; }
        public static IdleDetector Idle { get; private set; }
        public static TaskSchedulerService TaskScheduler { get; private set; }
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static NotifyIcon _trayIcon;

        private bool _sessionLocked;
        private DateTime _pauseUntil = DateTime.MinValue;
        private readonly List<ToolStripMenuItem> _idleItems = new List<ToolStripMenuItem>();
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem _tasksEnabledItem;
        private ToolStripMenuItem _manualMenu;
        private ToolStripMenuItem _recentMenu;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                Shutdown(0);
                return;
            }

            DispatcherUnhandledException += OnDispatcherException;

            Config = new ConfigService();
            Config.LoadOrCreate();

            Controller = new LockController(Config);

            if (!Config.Current.HasPin())
            {
                var wizard = new FirstRunWindow();
                if (wizard.ShowDialog() == true)
                {
                    Controller.Pins.SetNewPin(wizard.NewPin);
                    Config.Current.PinSalt = Controller.Pins.Salt;
                    Config.Current.PinHash = Controller.Pins.Hash;
                    Config.Save();
                }
                else
                {
                    Shutdown(0);
                    return;
                }
            }

            AutoStartService.Sync(Config.Current.AutoStart);

            CreateTrayIcon();

            Idle = new IdleDetector();
            Idle.Threshold = TimeSpan.FromMinutes(Config.Current.IdleMinutes);
            Idle.WarnBefore = TimeSpan.FromSeconds(30);
            Idle.ShouldSuspend = ShouldSuspendIdle;
            Idle.Warning += OnIdleWarning;
            Idle.ThresholdReached += OnIdleThresholdReached;
            Idle.Start();

            Controller.Unlocked += () =>
            {
                Idle.Reset();
                UpdateTrayText();
            };

            SystemEvents.SessionSwitch += OnSessionSwitch;

            // AutoRun tasks - independent shell, logs to logs/task-*.log
            try
            {
                TaskScheduler = new TaskSchedulerService(Idle);
                // ensure scripts dir exists early
                try { System.IO.Directory.CreateDirectory(ConfigService.ScriptsDirPath); } catch { }
                TaskScheduler.Start();
                // sync tray toggle with actual scheduler state (persisted)
                try
                {
                    if (_tasksEnabledItem != null)
                        _tasksEnabledItem.Checked = TaskScheduler.IsGlobalEnabled;
                }
                catch { }
                try { RefreshTaskMenu(); } catch { }
            }
            catch (Exception ex) { LogError(ex); }

            Exit += OnAppExit;

            UpdateTrayText();
        }

        private bool ShouldSuspendIdle()
        {
            return _sessionLocked
                || DateTime.Now < _pauseUntil
                || IdleDetector.IsSystemBusy();
        }

        private void OnIdleThresholdReached()
        {
            if (_sessionLocked) return;
            Controller.LockSafe();
        }

        private void OnIdleWarning()
        {
            if (_trayIcon == null) return;
            ShowBalloon(string.Format("空闲 {0} 分钟后自动锁定，动一下鼠标可取消", Config.Current.IdleMinutes));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsShuttingDown || Idle == null || Controller == null) return;
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _sessionLocked = true;
                    Idle.Suspend();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _sessionLocked = false;
                    Idle.Reset();
                }
                else if (e.Reason == SessionSwitchReason.RemoteDisconnect)
                {
                    Controller.LockSafe();
                }
            }));
        }

        private void ReloadConfig()
        {
            var ok = Config.Reload();
            var c = Config.Current;
            Idle.Threshold = TimeSpan.FromMinutes(c.IdleMinutes);
            Idle.Reset();
            Controller.ApplyPinFromConfig();
            AutoStartService.Sync(c.AutoStart);
            RefreshMenuChecks();
            UpdateTrayText();
            if (_trayIcon != null)
            {
                _trayIcon.BalloonTipTitle = "ScreenLock";
                _trayIcon.BalloonTipText = ok
                    ? string.Format("配置已重新加载：空闲 {0} 分钟后锁定", c.IdleMinutes)
                    : "配置加载失败，已保留上次有效配置";
                _trayIcon.ShowBalloonTip(2000);
            }
        }

        private void ReloadTasks()
        {
            if (TaskScheduler == null)
            {
                ShowBalloon("任务调度器未初始化");
                return;
            }
            var result = TaskScheduler.Reload();
            // sync global toggle if config was edited externally
            try
            {
                if (_tasksEnabledItem != null && TaskScheduler != null)
                {
                    bool global = TaskScheduler.IsGlobalEnabled;
                    if (_tasksEnabledItem.Checked != global)
                        _tasksEnabledItem.Checked = global;
                }
            }
            catch { }
            try { RefreshTaskMenu(); } catch { }
            var msg = result.Errors.Count == 0
                ? string.Format("任务已重载：{0} 个生效", result.Tasks.Count)
                : string.Format("任务重载完成：{0} 个生效，{1} 个错误", result.Tasks.Count, result.Errors.Count);
            if (result.Errors.Count > 0)
                msg += "，详见 logs/tasks.log";
            if (TaskScheduler != null && !TaskScheduler.IsGlobalEnabled)
                msg += "（总开关已禁用）";
            ShowBalloon(msg);
        }

        public void RefreshTaskMenu()
        {
            if (_manualMenu == null || TaskScheduler == null) return;
            try
            {
                _manualMenu.DropDownItems.Clear();
                var manuals = TaskScheduler.GetManualTasks();
                if (manuals.Count == 0)
                {
                    var empty = new ToolStripMenuItem("暂无手动任务") { Enabled = false };
                    _manualMenu.DropDownItems.Add(empty);
                }
                else
                {
                    foreach (var t in manuals)
                    {
                        var name = t.Name;
                        var item = new ToolStripMenuItem(name);
                        // show hotkey if any
                        if (t.Trigger.Type == ScreenLock.Models.TaskTriggerType.Hotkey && !string.IsNullOrWhiteSpace(t.Trigger.Hotkey))
                            item.ToolTipText = "热键: " + t.Trigger.Hotkey;
                        item.Click += (s, e) =>
                        {
                            bool ok = TaskScheduler.RunManual(name);
                            ShowBalloon(ok ? "已触发任务: " + name : "任务未找到或已禁用: " + name);
                        };
                        _manualMenu.DropDownItems.Add(item);
                    }
                }
                // recent
                if (_recentMenu != null)
                {
                    _recentMenu.DropDownItems.Clear();
                    var recents = TaskScheduler.GetRecent();
                    if (recents.Count == 0)
                    {
                        _recentMenu.DropDownItems.Add(new ToolStripMenuItem("暂无记录") { Enabled = false });
                    }
                    else
                    {
                        foreach (var r in recents)
                        {
                            var rItem = new ToolStripMenuItem(r) { Enabled = false };
                            _recentMenu.DropDownItems.Add(rItem);
                        }
                    }
                }
            }
            catch { }
        }

        public static void ShowBalloonPublic(string text)
        {
            try
            {
                var app = Current as App;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() => app.ShowBalloon(text)));
                else
                {
                    // fallback if no app
                }
            }
            catch { }
        }

        private void CreateTrayIcon()
        {
            var menu = new ContextMenuStrip();

            var lockItem = new ToolStripMenuItem("立即锁定") { Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold) };
            lockItem.Click += (s, e) => Controller.LockSafe();

            var reloadItem = new ToolStripMenuItem("Reload Config");
            reloadItem.Click += (s, e) => ReloadConfig();

            var reloadTasksItem = new ToolStripMenuItem("重载任务 (tasks.json)");
            reloadTasksItem.Click += (s, e) => ReloadTasks();

            _tasksEnabledItem = new ToolStripMenuItem("启用任务调度")
            {
                CheckOnClick = true,
                Checked = Config.Current.TasksEnabled
            };
            // sync scheduler global switch (in case Start already read config)
            try { if (TaskScheduler != null) TaskScheduler.SetGlobalEnabled(_tasksEnabledItem.Checked); } catch { }
            _tasksEnabledItem.CheckedChanged += (s, e) =>
            {
                bool enabled = _tasksEnabledItem.Checked;
                try { if (TaskScheduler != null) TaskScheduler.SetGlobalEnabled(enabled); } catch { }
                try
                {
                    Config.Current.TasksEnabled = enabled;
                    Config.Save();
                    ShowBalloon(enabled ? "任务调度已启用" : "任务调度已禁用");
                }
                catch { }
            };

            var openScriptsItem = new ToolStripMenuItem("打开 scripts 目录");
            openScriptsItem.Click += (s, e) =>
            {
                try
                {
                    var dir = ConfigService.ScriptsDirPath;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(dir);
                }
                catch { }
            };

            var openDirItem = new ToolStripMenuItem("打开配置目录");
            openDirItem.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(ConfigService.DirPath); } catch { }
            };

            var openTasksItem = new ToolStripMenuItem("编辑 tasks.json");
            openTasksItem.Click += (s, e) =>
            {
                try
                {
                    var path = ConfigService.TaskFilePath;
                    if (!File.Exists(path))
                    {
                        try { TaskConfigService.LoadOrCreate(); } catch { }
                    }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch { }
            };

            var openLogsItem = new ToolStripMenuItem("打开 logs 目录");
            openLogsItem.Click += (s, e) =>
            {
                try
                {
                    var dir = ConfigService.LogsDirPath;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(dir);
                }
                catch { }
            };

            var exitItem = new ToolStripMenuItem("退出...");
            exitItem.Click += OnExitClick;

            menu.Items.Add(lockItem);
            menu.Items.Add(new ToolStripSeparator());

            var idleMenu = new ToolStripMenuItem("空闲锁定");
            var presets = new[] { 0, 1, 3, 5, 10, 15, 30 };
            foreach (var m in presets)
            {
                var minutes = m;
                var item = new ToolStripMenuItem(minutes == 0 ? "禁用" : minutes + " 分钟") { Tag = minutes };
                item.Click += (s, e) => SetIdleMinutes(minutes);
                _idleItems.Add(item);
                idleMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(idleMenu);

            var pauseMenu = new ToolStripMenuItem("暂停计时");
            var pause30Item = new ToolStripMenuItem("暂停 30 分钟");
            pause30Item.Click += (s, e) => PauseFor(TimeSpan.FromMinutes(30));
            var pause60Item = new ToolStripMenuItem("暂停 1 小时");
            pause60Item.Click += (s, e) => PauseFor(TimeSpan.FromHours(1));
            var resumeItem = new ToolStripMenuItem("恢复计时");
            resumeItem.Click += (s, e) => ResumeIdle();
            pauseMenu.DropDownItems.Add(pause30Item);
            pauseMenu.DropDownItems.Add(pause60Item);
            pauseMenu.DropDownItems.Add(resumeItem);
            menu.Items.Add(pauseMenu);

            menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机自启")
            {
                CheckOnClick = true,
                Checked = Config.Current.AutoStart
            };
            _autoStartItem.CheckedChanged += (s, e) =>
            {
                Config.Current.AutoStart = _autoStartItem.Checked;
                AutoStartService.Sync(_autoStartItem.Checked);
                Config.Save();
            };
            menu.Items.Add(_autoStartItem);

            _manualMenu = new ToolStripMenuItem("手动运行");
            _recentMenu = new ToolStripMenuItem("最近运行");

            var editorItem = new ToolStripMenuItem("任务编辑器...");
            editorItem.Click += (s, e) =>
            {
                try
                {
                    var win = new Views.TaskEditorWindow();
                    win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    win.ShowDialog();
                    // after editor closed, refresh manual menu
                    try { RefreshTaskMenu(); } catch { }
                }
                catch (Exception ex) { LogError(ex); }
            };

            var taskMenu = new ToolStripMenuItem("任务");
            taskMenu.DropDownItems.Add(_tasksEnabledItem);
            taskMenu.DropDownItems.Add(new ToolStripSeparator());
            taskMenu.DropDownItems.Add(_manualMenu);
            taskMenu.DropDownItems.Add(_recentMenu);
            taskMenu.DropDownItems.Add(new ToolStripSeparator());
            taskMenu.DropDownItems.Add(editorItem);
            taskMenu.DropDownItems.Add(reloadTasksItem);
            taskMenu.DropDownItems.Add(openTasksItem);
            taskMenu.DropDownItems.Add(openScriptsItem);
            taskMenu.DropDownItems.Add(openLogsItem);

            menu.Items.Add(reloadItem);
            menu.Items.Add(taskMenu);
            menu.Items.Add(openDirItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            RefreshMenuChecks();

            _trayIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "ScreenLock",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (s, e) => Controller.LockSafe();
        }

        private void SetIdleMinutes(int minutes)
        {
            Config.Current.IdleMinutes = minutes;
            Config.Save();
            Idle.Threshold = TimeSpan.FromMinutes(minutes);
            Idle.Reset();
            RefreshMenuChecks();
            UpdateTrayText();
        }

        private void PauseFor(TimeSpan duration)
        {
            _pauseUntil = DateTime.Now.Add(duration);
            Idle.Reset();
            UpdateTrayText();
            ShowBalloon(string.Format("已暂停自动锁定，{0:HH:mm} 后恢复", _pauseUntil));
        }

        private void ResumeIdle()
        {
            _pauseUntil = DateTime.MinValue;
            Idle.Reset();
            UpdateTrayText();
        }

        private void RefreshMenuChecks()
        {
            foreach (var item in _idleItems)
                item.Checked = (int)item.Tag == Config.Current.IdleMinutes;
            if (_autoStartItem != null)
                _autoStartItem.Checked = Config.Current.AutoStart;
        }

        private void ShowBalloon(string text)
        {
            if (_trayIcon == null) return;
            try
            {
                _trayIcon.BalloonTipTitle = "ScreenLock";
                _trayIcon.BalloonTipText = text;
                _trayIcon.ShowBalloonTip(3000);
            }
            catch { }
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Icon.ico");
                using (var stream = GetResourceStream(uri).Stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
            catch
            {
                return System.Drawing.SystemIcons.Shield;
            }
        }

        private void UpdateTrayText()
        {
            if (_trayIcon == null) return;
            try
            {
                string text;
                if (DateTime.Now < _pauseUntil)
                    text = string.Format("ScreenLock - 自动锁定已暂停至 {0:HH:mm}", _pauseUntil);
                else if (Config.Current.IdleMinutes <= 0)
                    text = "ScreenLock - 空闲锁定已禁用";
                else
                    text = string.Format("ScreenLock - 空闲 {0} 分钟锁定", Config.Current.IdleMinutes);
                _trayIcon.Text = text;
            }
            catch { }
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            var win = new VerifyPinWindow(Controller, "退出 ScreenLock 需要验证 PIN")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            if (win.ShowDialog() == true)
                ExitApp();
        }

        public static void ExitApp()
        {
            IsShuttingDown = true;
            try
            {
                Current.Dispatcher.BeginInvoke(new Action(() => Current.Shutdown()));
            }
            catch { }
        }

        private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            e.Handled = true;
        }

        private static void LogError(Exception ex)
        {
            try
            {
                var path = Path.Combine(ConfigService.DirPath, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Delete(path);
                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
            }
            catch { }
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            IsShuttingDown = true;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { if (TaskScheduler != null) TaskScheduler.Stop(); } catch { }
            try { if (TaskScheduler != null) TaskScheduler.Dispose(); } catch { }
            try { if (Controller != null) Controller.Dispose(); } catch { }
            try { if (Idle != null) Idle.Dispose(); } catch { }
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
            }
            catch { }
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }
}
