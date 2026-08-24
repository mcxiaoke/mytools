using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScreenLock.Services
{
    public class IdleDetector : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private readonly DispatcherTimer _timer;
        private bool _fired;

        public TimeSpan Threshold { get; set; }
        public event Action ThresholdReached;

        public IdleDetector()
        {
            Threshold = TimeSpan.FromMinutes(5);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _fired = false;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public void Reset()
        {
            _fired = false;
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
            if (_fired) return;
            var idle = TimeSpan.FromMilliseconds(GetIdleMilliseconds());
            if (idle >= Threshold)
            {
                _fired = true;
                var handler = ThresholdReached;
                if (handler != null) handler();
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
