using System.Security.Cryptography;

namespace HideFiles.Core;

/// <summary>
/// v2 密钥派生与 GCM 工具。全部基于 .NET BCL，无第三方依赖。
///
/// 密钥层次：
///   PIN（低熵） + configSalt --PBKDF2--> KEK --AES-GCM(MK)--> emkBlob
///   MK（高熵随机，= 恢复码） + fileSalt --HKDF--> fileKey --> 加密元数据/主体
///
/// 文件自包含：emkBlob 存于每个文件头部，拷到任何机器只要有 PIN 即可还原；
/// 忘记 PIN 时可用恢复码（MK 的 Base32）直接解密。
/// </summary>
public static class V2Crypto
{
    public static byte[] DeriveKek(string pin, byte[] configSalt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            NormalizePin(pin), configSalt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(CoreConstants.MasterKeySize);
    }

    /// <summary>HKDF-SHA256：从 MK 派生每文件 fileKey（MK 已高熵，此步极快）</summary>
    public static byte[] DeriveFileKey(byte[] masterKey, byte[] fileSalt)
    {
        // 使用 HMAC-SHA256 做一次性派生：fileKey = HMAC(MK, "HF2-FILEKEY" || fileSalt)
        byte[] prefix = "HF2-FILEKEY"u8.ToArray();
        byte[] data = new byte[prefix.Length + fileSalt.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(fileSalt, 0, data, prefix.Length, fileSalt.Length);
        return HMACSHA256.HashData(masterKey, data);
    }

    /// <summary>用 KEK 加密 MK，产出 60 字节 emkBlob：nonce(12)+cipher(32)+tag(16)</summary>
    public static byte[] WrapMasterKey(byte[] kek, byte[] masterKey)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(CoreConstants.NonceSize);
        byte[] cipher = new byte[CoreConstants.MasterKeySize];
        byte[] tag = new byte[CoreConstants.GcmTagSize];
        using (var gcm = new AesGcm(kek, CoreConstants.GcmTagSize))
            gcm.Encrypt(nonce, masterKey, cipher, tag);

        byte[] blob = new byte[CoreConstants.EmkBlobSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, blob, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, 44, tag.Length);
        return blob;
    }

    /// <summary>用 KEK 解开 emkBlob 得到 MK；PIN 错误会抛 AuthenticationTagMismatchException</summary>
    public static byte[] UnwrapMasterKey(byte[] kek, byte[] blob)
    {
        if (blob.Length != CoreConstants.EmkBlobSize)
            throw new InvalidDataException("Invalid EMK blob length.");
        byte[] nonce = blob.AsSpan(0, 12).ToArray();
        byte[] cipher = blob.AsSpan(12, 32).ToArray();
        byte[] tag = blob.AsSpan(44, 16).ToArray();
        byte[] mk = new byte[CoreConstants.MasterKeySize];
        using (var gcm = new AesGcm(kek, CoreConstants.GcmTagSize))
            gcm.Decrypt(nonce, cipher, tag, mk);
        return mk;
    }

    /// <summary>尝试用 KEK 解开 emkBlob；PIN 不匹配时返回 false 而不是抛异常</summary>
    public static bool TryUnwrapMasterKey(byte[] kek, byte[] blob, out byte[] masterKey)
    {
        masterKey = Array.Empty<byte>();
        if (blob.Length != CoreConstants.EmkBlobSize) return false;
        try
        {
            masterKey = UnwrapMasterKey(kek, blob);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    /// <summary>以 fileKey 生成一个"派生 nonce"：基础 nonce 与块序号异或，保证同文件内 nonce 唯一</summary>
    public static byte[] BlockNonce(byte[] baseNonce, ulong blockIndex)
    {
        byte[] n = (byte[])baseNonce.Clone();
        // 低 8 字节写入 blockIndex（大端），与 baseNonce 低 8 字节异或
        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)(blockIndex >> (8 * (7 - i)));
            n[CoreConstants.NonceSize - 8 + i] ^= b;
        }
        return n;
    }

    /// <summary>
    /// 原地 AES-CTR 变换（加密与解密同一操作，长度不变）。
    /// counter block = nonce(12) || 4 字节大端块序号（从 0 开始，按区内偏移计算）。
    /// 结构锁只有一个 CTR 实现，避免多份拷贝各自漂移。
    /// </summary>
    public static void CtrTransform(Span<byte> data, byte[] key, byte[] nonce)
    {
        byte[] counter = new byte[16];
        Buffer.BlockCopy(nonce, 0, counter, 0, Math.Min(nonce.Length, CoreConstants.NonceSize));
        byte[] ks = new byte[16];
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var ecb = aes.CreateEncryptor();

        uint ordinal = 0;
        int blocks = data.Length / 16;
        for (int b = 0; b < blocks; b++)
        {
            counter[15] = (byte)ordinal;
            counter[14] = (byte)(ordinal >> 8);
            counter[13] = (byte)(ordinal >> 16);
            counter[12] = (byte)(ordinal >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = b * 16;
            for (int i = 0; i < 16; i++) data[off + i] ^= ks[i];
            ordinal++;
        }

        int rem = data.Length % 16;
        if (rem > 0)
        {
            counter[15] = (byte)ordinal;
            counter[14] = (byte)(ordinal >> 8);
            counter[13] = (byte)(ordinal >> 16);
            counter[12] = (byte)(ordinal >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = blocks * 16;
            for (int i = 0; i < rem; i++) data[off + i] ^= ks[i];
        }
    }

    private static byte[] NormalizePin(string pin) => System.Text.Encoding.UTF8.GetBytes(pin.Trim());

    /// <summary>生成恢复码：MK 的 Base32（无填充、大写、易抄写）</summary>
    public static string MasterKeyToRecoveryCode(byte[] masterKey) => Base32.Encode(masterKey);

    /// <summary>从恢复码还原 MK；非法字符串抛异常</summary>
    public static byte[] RecoveryCodeToMasterKey(string code) => Base32.Decode(code);

    /// <summary>
    /// 解析出用于解开某个文件的主密钥。优先顺序：
    /// 1. 恢复码（直接就是 MK）；
    /// 2. 文件自带的 emkBlob + 用户 PIN（文件可脱离本机搬运）；
    /// 3. 本机 config 中当前 PIN 对应的主密钥（用户改过 PIN 后的兜底，文件里的 emkBlob 仍是旧 PIN 封装的）。
    /// 拿到的密钥是否正确，最终由元数据的 GCM 认证标签判定。
    /// </summary>
    public static byte[] ResolveMasterKey(string? pin, string? recoveryCode, byte[] configSalt,
        byte[] emkBlob, byte[]? fallbackMasterKey)
    {
        if (!string.IsNullOrEmpty(recoveryCode))
            return RecoveryCodeToMasterKey(recoveryCode);

        if (!string.IsNullOrEmpty(pin))
        {
            byte[] kek = DeriveKek(pin, configSalt, CoreConstants.DefaultKdfIterations);
            if (TryUnwrapMasterKey(kek, emkBlob, out byte[] mk)) return mk;
        }

        if (fallbackMasterKey != null && fallbackMasterKey.Length == CoreConstants.MasterKeySize)
            return fallbackMasterKey;

        if (string.IsNullOrEmpty(pin))
            throw new ArgumentException("PIN or recovery code is required.");

        throw new InvalidDataException("PIN 或恢复码与该文件不匹配，无法解开文件密钥。");
    }
}

/// <summary>无依赖的 Base32（RFC 4648，无填充）编解码</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int bits = 0, value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>解码恢复码：结果必须正好是 32 字节主密钥</summary>
    public static byte[] Decode(string text)
    {
        byte[] result = DecodeVariableLength(text);
        if (result.Length != CoreConstants.MasterKeySize)
            throw new FormatException("Invalid recovery code length.");
        return result;
    }

    /// <summary>解码任意长度的 Base32 文本（供目录名混淆等场景使用）</summary>
    public static byte[] DecodeVariableLength(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Base32 input is empty.");
        string s = text.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        using var ms = new MemoryStream();
        int bits = 0, value = 0;
        foreach (char c in s)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid Base32 character '{c}'.");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                ms.WriteByte((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return ms.ToArray();
    }
}
