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

    private static byte[] NormalizePin(string pin) => System.Text.Encoding.UTF8.GetBytes(pin.Trim());

    /// <summary>生成恢复码：MK 的 Base32（无填充、大写、易抄写）</summary>
    public static string MasterKeyToRecoveryCode(byte[] masterKey) => Base32.Encode(masterKey);

    /// <summary>从恢复码还原 MK；非法字符串抛异常</summary>
    public static byte[] RecoveryCodeToMasterKey(string code) => Base32.Decode(code);
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

    public static byte[] Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Recovery code is empty.");
        string s = text.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        using var ms = new MemoryStream();
        int bits = 0, value = 0;
        foreach (char c in s)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid recovery code character '{c}'.");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                ms.WriteByte((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        byte[] result = ms.ToArray();
        if (result.Length != CoreConstants.MasterKeySize)
            throw new FormatException("Invalid recovery code length.");
        return result;
    }
}
