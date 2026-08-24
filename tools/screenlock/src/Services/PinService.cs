using System;
using System.Security.Cryptography;
using System.Text;

namespace ScreenLock.Services
{
    public class PinService
    {
        public string Salt { get; private set; }
        public string Hash { get; private set; }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash); }
        }

        public void SetFromConfig(string salt, string hash)
        {
            Salt = salt;
            Hash = hash;
        }

        public void SetNewPin(string pin)
        {
            var salt = GenerateSalt();
            Salt = Convert.ToBase64String(salt);
            Hash = ComputeHash(salt, pin);
        }

        public static byte[] GenerateSalt()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                var data = new byte[16];
                rng.GetBytes(data);
                return data;
            }
        }

        public static string ComputeHash(byte[] salt, string pin)
        {
            using (var sha = SHA256.Create())
            {
                var input = Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + ":" + pin);
                var digest = sha.ComputeHash(input);
                return Convert.ToBase64String(digest);
            }
        }

        public bool Verify(string pin)
        {
            if (!IsConfigured) return false;
            byte[] salt;
            try { salt = Convert.FromBase64String(Salt); }
            catch { return false; }
            var candidate = ComputeHash(salt, pin);
            return FixedTimeEquals(candidate, Hash);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public enum PinAttemptResult
    {
        Success,
        Wrong,
        Blocked
    }

    public class PinGuard
    {
        private const int MaxFreeAttempts = 5;

        private readonly PinService _pin;
        private int _fails;
        private DateTime _blockedUntil = DateTime.MinValue;

        public PinGuard(PinService pin)
        {
            _pin = pin;
        }

        public void Reload(PinService pin)
        {
            _fails = 0;
            _blockedUntil = DateTime.MinValue;
        }

        public TimeSpan RemainingBlock()
        {
            var left = _blockedUntil - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        public PinAttemptResult Try(string pin, out TimeSpan blockRemaining)
        {
            blockRemaining = RemainingBlock();
            if (blockRemaining > TimeSpan.Zero) return PinAttemptResult.Blocked;

            if (_pin.Verify(pin))
            {
                Reset();
                return PinAttemptResult.Success;
            }

            _fails++;
            if (_fails >= MaxFreeAttempts)
                _blockedUntil = DateTime.Now.AddSeconds((_fails - MaxFreeAttempts + 1) * 30.0);
            return PinAttemptResult.Wrong;
        }

        public void Reset()
        {
            _fails = 0;
            _blockedUntil = DateTime.MinValue;
        }
    }
}
