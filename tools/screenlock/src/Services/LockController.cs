using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ScreenLock.Views;

namespace ScreenLock.Services
{
    public class LockController : IDisposable
    {
        private readonly ConfigService _config;
        private readonly PinService _pinService;
        private readonly PinGuard _pinGuard;
        private readonly KeyboardBlocker _blocker;

        private readonly List<LockWindow> _lockWindows = new List<LockWindow>();
        private bool _locked;

        public event Action Unlocked;

        public LockController(ConfigService config)
        {
            _config = config;
            _pinService = new PinService();
            _blocker = new KeyboardBlocker();
            _pinGuard = new PinGuard(_pinService);
            ApplyPinFromConfig();
        }

        public bool IsLocked { get { return _locked; } }

        public TimeSpan GetBlockRemaining()
        {
            return _locked ? _pinGuard.RemainingBlock() : TimeSpan.Zero;
        }

        public PinService Pins { get { return _pinService; } }

        public void ApplyPinFromConfig()
        {
            var c = _config.Current;
            _pinService.SetFromConfig(c.PinSalt, c.PinHash);
        }

        public void Lock()
        {
            if (_locked || !_pinService.IsConfigured) return;
            _locked = true;
            _blocker.Install();

            foreach (Screen screen in Screen.AllScreens)
            {
                var win = new LockWindow(this, screen, screen.Primary);
                _lockWindows.Add(win);
                win.Show();
                win.ActivateIfNeeded();
            }
        }

        public void LockSafe()
        {
            try
            {
                Lock();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        public PinAttemptResult TryUnlock(string pin, out string error)
        {
            TimeSpan remaining;
            var result = _pinGuard.Try(pin, out remaining);
            if (result == PinAttemptResult.Success)
            {
                error = null;
                Unlock();
                return PinAttemptResult.Success;
            }
            if (result == PinAttemptResult.Blocked)
            {
                error = string.Format("尝试次数过多，请等待 {0:mm\\:ss} 后重试", remaining);
                return PinAttemptResult.Blocked;
            }
            error = "PIN 错误";
            if (_pinGuard.RemainingBlock() > TimeSpan.Zero)
            {
                var left = _pinGuard.RemainingBlock();
                error += string.Format("，已锁定 {0:mm\\:ss}", left);
            }
            return PinAttemptResult.Wrong;
        }

        public void Unlock()
        {
            if (!_locked) return;
            _locked = false;
            foreach (var win in _lockWindows)
            {
                win.CloseSafe();
            }
            _lockWindows.Clear();
            _blocker.Remove();

            var handler = Unlocked;
            if (handler != null) handler();
        }

        public bool VerifyForExit(string pin)
        {
            return _pinService.Verify(pin);
        }

        public void Dispose()
        {
            Unlock();
            _blocker.Dispose();
        }
    }
}
