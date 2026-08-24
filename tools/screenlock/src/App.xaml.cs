using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenLock.Services;
using ScreenLock.Views;

namespace ScreenLock
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\ScreenLock_SingleInstance_2C7A4F10";

        public static ConfigService Config { get; private set; }
        public static LockController Controller { get; private set; }
        public static IdleDetector Idle { get; private set; }
        public static bool IsShuttingDown { get; private set; }

        private static Mutex _mutex;
        private static NotifyIcon _trayIcon;

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
            Idle.ThresholdReached += OnIdleThresholdReached;
            Idle.Start();

            Controller.Unlocked += () =>
            {
                Idle.Reset();
                UpdateTrayText();
            };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Exit += OnAppExit;

            UpdateTrayText();
        }

        private void OnIdleThresholdReached()
        {
            Controller.LockSafe();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsShuttingDown || Controller == null || Idle == null) return;
                var idle = TimeSpan.FromMilliseconds(IdleDetector.GetIdleMilliseconds());
                if (!Controller.IsLocked && idle >= Idle.Threshold)
                    Controller.LockSafe();
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

        private void CreateTrayIcon()
        {
            var menu = new ContextMenuStrip();

            var lockItem = new ToolStripMenuItem("立即锁定") { Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold) };
            lockItem.Click += (s, e) => Controller.LockSafe();

            var reloadItem = new ToolStripMenuItem("Reload Config");
            reloadItem.Click += (s, e) => ReloadConfig();

            var openDirItem = new ToolStripMenuItem("打开配置目录");
            openDirItem.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(ConfigService.DirPath); } catch { }
            };

            var exitItem = new ToolStripMenuItem("退出...");
            exitItem.Click += OnExitClick;

            menu.Items.Add(lockItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(reloadItem);
            menu.Items.Add(openDirItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "ScreenLock",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (s, e) => Controller.LockSafe();
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
                _trayIcon.Text = string.Format("ScreenLock - 空闲 {0} 分钟锁定", Config.Current.IdleMinutes);
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
                File.AppendAllText(
                    Path.Combine(ConfigService.DirPath, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
            }
            catch { }
        }

        private void OnAppExit(object sender, ExitEventArgs e)
        {
            IsShuttingDown = true;
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
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
