using System;
using System.Security.Cryptography;
using System.Text;

namespace ScreenLock.Services
{
    public class PinService
    {
        private const int Pbkdf2Iterations = 100000;

        public string Salt { get; private set; }
        public string Hash { get; private set; }

        public bool JustUpgraded { get; private set; }

        public void ClearUpgraded()
        {
            JustUpgraded = false;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash); }
        }

        public void SetFromConfig(string salt, string hash)
        {
            Salt = salt;
            Hash = hash;
            JustUpgraded = false;
        }

        public void SetNewPin(string pin)
        {
            var salt = GenerateSalt();
            Salt = Convert.ToBase64String(salt);
            Hash = ComputeHash(salt, pin);
            JustUpgraded = false;
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
            var password = Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + ":" + pin);
            using (var derive = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256))
            {
                return "pbkdf2$" + Pbkdf2Iterations + "$" + Convert.ToBase64String(derive.GetBytes(32));
            }
        }

        private static string ComputeLegacyHash(byte[] salt, string pin)
        {
            using (var sha = SHA256.Create())
            {
                var input = Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + ":" + pin);
                return Convert.ToBase64String(sha.ComputeHash(input));
            }
        }

        public bool Verify(string pin)
        {
            if (!IsConfigured) return false;
            byte[] salt;
            try { salt = Convert.FromBase64String(Salt); }
            catch { return false; }
            var candidate = ComputeHash(salt, pin);
            if (FixedTimeEquals(candidate, Hash)) return true;

            // 兼容旧版单轮 SHA256 哈希，验证通过后透明升级为 PBKDF2
            if (FixedTimeEquals(ComputeLegacyHash(salt, pin), Hash))
            {
                Hash = candidate;
                JustUpgraded = true;
                return true;
            }
            return false;
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
