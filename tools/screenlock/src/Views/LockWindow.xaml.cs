using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenLock.Services;

namespace ScreenLock.Views
{
    public partial class LockWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const uint SWP_NOMOVE = 0x2;
        private const uint SWP_NOSIZE = 0x1;
        private const uint SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private readonly LockController _controller;
        private readonly bool _primary;
        private readonly System.Drawing.Rectangle _bounds;
        private readonly DispatcherTimer _keepAliveTimer;
        private readonly DispatcherTimer _uiTimer;

        public LockWindow(LockController controller, System.Windows.Forms.Screen screen, bool primary)
        {
            InitializeComponent();
            _controller = controller;
            _primary = primary;
            _bounds = screen.Bounds;

            Left = screen.Bounds.Left;
            Top = screen.Bounds.Top;
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;

            RootBorder.Opacity = App.Config.Current.OverlayOpacity;

            InputPanel.Visibility = primary ? Visibility.Visible : Visibility.Collapsed;
            CoverPanel.Visibility = primary ? Visibility.Collapsed : Visibility.Visible;
            Cursor = primary ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.None;
            ClockText.Visibility = App.Config.Current.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            CoverClockText.Visibility = ClockText.Visibility;

            _keepAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _keepAliveTimer.Tick += OnKeepAliveTick;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += OnUiTick;

            Loaded += OnLoaded;
            Deactivated += OnDeactivated;
            Closing += OnClosing;

            UnlockButton.Click += OnUnlockClick;
            PinBox.KeyDown += OnPinKeyDown;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            _keepAliveTimer.Start();
            _uiTimer.Start();

            if (_primary)
            {
                PinBox.Focus();
                UpdateClock();
            }
        }

        private void OnKeepAliveTick(object sender, EventArgs e)
        {
            if (App.IsShuttingDown) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            if (!_primary) return;
            UpdateClock();
            UpdatePenaltyState();
        }

        private void UpdateClock()
        {
            if (!App.Config.Current.ShowClock) return;
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ClockText.Text = now;
            CoverClockText.Text = now;
        }

        private void UpdatePenaltyState()
        {
            var blocked = App.Controller.GetBlockRemaining();
            bool blockedNow = blocked > TimeSpan.Zero;
            if (blockedNow)
            {
                PinBox.IsEnabled = false;
                UnlockButton.IsEnabled = false;
                MessageText.Text = string.Format("尝试次数过多，{0:mm\\:ss} 后可重试", blocked);
            }
            else if (PinBox.IsEnabled == false)
            {
                PinBox.IsEnabled = true;
                UnlockButton.IsEnabled = true;
                MessageText.Text = "";
                PinBox.Clear();
                PinBox.Focus();
            }
        }

        public void ActivateIfNeeded()
        {
            if (_primary) Activate();
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            if (!_primary) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Activate(); } catch { }
            }), DispatcherPriority.ApplicationIdle);
        }

        private void OnPinKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                TryUnlock();
                e.Handled = true;
            }
        }

        private void OnUnlockClick(object sender, RoutedEventArgs e)
        {
            TryUnlock();
        }

        private void TryUnlock()
        {
            string error;
            var result = _controller.TryUnlock(PinBox.Password, out error);
            if (result == PinAttemptResult.Success) return;
            MessageText.Text = error;
            PinBox.Clear();
            PinBox.Focus();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _keepAliveTimer.Stop();
            _uiTimer.Stop();
        }

        public void CloseSafe()
        {
            try
            {
                Close();
            }
            catch { }
        }
    }
}
