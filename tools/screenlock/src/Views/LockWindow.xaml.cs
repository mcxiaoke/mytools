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
        private const uint GW_HWNDPREV = 3;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private readonly LockController _controller;
        private readonly bool _primary;
        private readonly System.Drawing.Rectangle _bounds;
        private readonly DispatcherTimer _keepAliveTimer;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _deactivateTimer;
        private DateTime _lastKeepAlive = DateTime.MinValue;
        private DateTime _lastActivateAttempt = DateTime.MinValue;
        private bool _isClosing;

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

            // 修复前 500ms 无条件 SetWindowPos(TOPMOST) 与网速等 TOPMOST 浮层抢 Z 序导致闪烁
            // 改为 2.5s 节流 + 仅当真正被覆盖时才置顶
            _keepAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _keepAliveTimer.Tick += OnKeepAliveTick;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += OnUiTick;

            // Deactivated 抢焦点防抖：400ms 延迟且节流 800ms，避免与浮层焦点抢占造成闪烁
            _deactivateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _deactivateTimer.Tick += OnDeactivateTimerTick;

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

            // 注册 WndProc 拦截 WM_WINDOWPOSCHANGING 以无闪烁方式维持 TOPMOST
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null)
                source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            if (msg == WM_WINDOWPOSCHANGING && !_isClosing && !App.IsShuttingDown)
            {
                try
                {
                    var pos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                    // 强制保持 TOPMOST 且不激活，避免 SetWindowPos 轮询带来的闪烁
                    // 仅在窗口试图去掉 TOPMOST 时修正，正常 DWM 合成不干预
                    pos.flags &= ~((uint)0x0020); // 去掉 SWP_NOZORDER
                    pos.hwndInsertAfter = HWND_TOPMOST;
                    Marshal.StructureToPtr(pos, lParam, true);
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
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

        private bool IsAlreadyOnTop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            // GW_HWNDPREV == 0 表示已在 Z 序最顶端（TOPMOST 组内最前），无需再 SetWindowPos
            IntPtr prev = GetWindow(hwnd, GW_HWNDPREV);
            return prev == IntPtr.Zero;
        }

        private void OnKeepAliveTick(object sender, EventArgs e)
        {
            if (App.IsShuttingDown || _isClosing) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;

            // 节流：距离上次置顶 <2s 则跳过，避免与网速浮层等 TOPMOST 窗口高频互抢导致闪烁
            if ((DateTime.Now - _lastKeepAlive).TotalMilliseconds < 2000) return;

            // 已在最前则不做任何操作，彻底消除无条件 SetWindowPos 引发的 DWM 重合成闪烁
            if (IsAlreadyOnTop(hwnd)) return;

            // 额外 guard：若当前前台窗口就是我们，避免重复置顶
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;

            _lastKeepAlive = DateTime.Now;
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
            if (!_primary || _isClosing || App.IsShuttingDown) return;
            // 防抖：不立即 Activate，延迟 400ms 并节流，避免与 TOPMOST 浮层焦点抢占导致闪烁
            _deactivateTimer.Stop();
            _deactivateTimer.Start();
        }

        private void OnDeactivateTimerTick(object sender, EventArgs e)
        {
            _deactivateTimer.Stop();
            if (_isClosing || App.IsShuttingDown) return;
            if (!_primary) return;
            // 节流 800ms，避免高频 Activate 造成任务栏/浮层闪烁
            if ((DateTime.Now - _lastActivateAttempt).TotalMilliseconds < 800) return;
            // 若我们已是前台或已被 KeepAlive 置顶，则不抢焦点
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd) return;
            if (hwnd != IntPtr.Zero && IsAlreadyOnTop(hwnd) && IsActive) return;

            _lastActivateAttempt = DateTime.Now;
            try { Activate(); } catch { }
            // 重新置顶但不激活，避免闪烁
            try
            {
                if (hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
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
            _isClosing = true;
            _keepAliveTimer.Stop();
            _uiTimer.Stop();
            _deactivateTimer.Stop();
        }

        public void CloseSafe()
        {
            try
            {
                _isClosing = true;
                Close();
            }
            catch { }
        }
    }
}
