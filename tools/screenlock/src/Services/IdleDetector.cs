using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScreenLock.Services
{
    public class IdleDetector : IDisposable
    {
        private enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

        public static bool IsSystemBusy()
        {
            try
            {
                QUERY_USER_NOTIFICATION_STATE s;
                if (SHQueryUserNotificationState(out s) != 0) return false;
                return s == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                    || s == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    || s == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private const int IntervalMs = 1000;

        private readonly DispatcherTimer _timer;
        private bool _fired;
        private bool _warned;
        private double _effectiveMs;
        private uint _lastRaw;

        public TimeSpan Threshold { get; set; }
        public TimeSpan WarnBefore { get; set; }
        public Func<bool> ShouldSuspend { get; set; }
        public event Action ThresholdReached;
        public event Action Warning;

        public IdleDetector()
        {
            Threshold = TimeSpan.FromMinutes(5);
            WarnBefore = TimeSpan.FromSeconds(30);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _fired = false;
            _warned = false;
            _effectiveMs = 0;
            _lastRaw = 0;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public void Reset()
        {
            _fired = false;
            _warned = false;
        }

        public void Suspend()
        {
            _fired = true;
        }

        public static uint GetIdleMilliseconds()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
            if (!GetLastInputInfo(ref info)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - info.dwTime);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_fired || Threshold <= TimeSpan.Zero) return;

            uint raw = GetIdleMilliseconds();
            bool hasInput = raw < _lastRaw;
            _lastRaw = raw;

            bool suspended = ShouldSuspend != null && ShouldSuspend();
            if (hasInput) _effectiveMs = raw;
            else if (!suspended) _effectiveMs += IntervalMs;
            if (suspended) return;

            var idle = TimeSpan.FromMilliseconds(_effectiveMs);
            if (idle >= Threshold)
            {
                _fired = true;
                var handler = ThresholdReached;
                if (handler != null) handler();
                return;
            }

            if (WarnBefore > TimeSpan.Zero && Threshold > WarnBefore)
            {
                if (idle >= Threshold - WarnBefore)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        var handler = Warning;
                        if (handler != null) handler();
                    }
                }
                else
                {
                    _warned = false;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
