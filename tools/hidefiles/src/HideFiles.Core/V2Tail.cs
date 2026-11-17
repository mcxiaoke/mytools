using System.Security.Cryptography;
using System.Text;

namespace HideFiles.Core;

/// <summary>
/// 结构锁模式的"原地加密 + 尾部容器"实现。
///
/// 与整体加密（前置容器）不同，结构锁不重写整个文件：
///   1. 原地加密"头部签名区"(前 64B，隐藏 ftyp/EBML/SOI/PNG 签名) + 格式关键结构区；
///   2. 在文件末尾追加一个固定尾部 footer（密钥材料 + 加密元数据）。
/// 只改动几 KB + 追加约几百字节，任意大小文件都接近秒级，无 2x 磁盘 IO。
///
/// Footer 布局（从文件末尾往前）：
///   [magic(8)][ver(1)][mode(1)][emkBlob(60)][fileNonce(12)][fileSalt(16)][configSalt(16)][metaLen(4)][metaCipher(metaLen)]
/// </summary>
public static class V2Tail
{
    private const int FixedTailSize = 8 + 1 + 1 + CoreConstants.EmkBlobSize + CoreConstants.NonceSize
                                      + CoreConstants.SaltSize + CoreConstants.SaltSize + 4; // = 118

    public static bool IsTailFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < FixedTailSize) return false;
            fs.Seek(fs.Length - CoreConstants.V2TailMagic.Length, SeekOrigin.Begin);
            byte[] m = new byte[CoreConstants.V2TailMagic.Length];
            fs.ReadExactly(m, 0, m.Length);
            return m.AsSpan().SequenceEqual(CoreConstants.V2TailMagic);
        }
        catch { return false; }
    }

    /// <summary>
    /// 原地加密当前文件（不重写整体），并在尾部追加 footer。
    /// 返回 footer 写入后的文件路径（与原路径相同）。
    /// </summary>
    public static void ProtectInPlace(string filePath, V2Metadata meta, string pin,
        byte[] masterKey, byte[] configSalt)
    {
        long origLen = new FileInfo(filePath).Length;
        meta.OriginalLength = origLen;
        meta.Mode = ProtectionMode.StructureLock;

        // 头部签名区 + 格式关键结构区，全部作为原地加密区
        var regions = new List<LockRegion>();
        int headLen = (int)Math.Min(CoreConstants.HeadSignatureSize, origLen);
        if (headLen > 0) regions.Add(new LockRegion(0, headLen, "HEADER-SIG"));
        foreach (var r in meta.LockRegions.OrderBy(r => r.Offset))
        {
            // 合并与头部区重叠的区域，避免二次加密
            if (r.Offset < headLen)
            {
                int end = (int)Math.Max(headLen, r.Offset + r.Length);
                regions[0].Length = Math.Min(end, (int)origLen);
                continue;
            }
            regions.Add(new LockRegion(r.Offset, r.Length, r.Kind));
        }
        meta.LockRegions = regions;

        byte[] fileSalt = RandomNumberGenerator.GetBytes(CoreConstants.SaltSize);
        byte[] fileNonce = RandomNumberGenerator.GetBytes(CoreConstants.NonceSize);
        byte[] fileKey = V2Crypto.DeriveFileKey(masterKey, fileSalt);

        // 1) 计算每个区的原始 CRC32（用于还原后快速校验，无需读全文件）
        meta.RegionCrcs = new List<uint>();
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var r in regions)
            {
                byte[] buf = new byte[r.Length];
                fs.Seek(r.Offset, SeekOrigin.Begin);
                fs.ReadExactly(buf, 0, buf.Length);
                meta.RegionCrcs.Add(Crc32Helper.Compute(buf));

                // 原地 CTR 加密
                byte[] nonce = V2Crypto.BlockNonce(fileNonce, (ulong)r.Offset);
                CtrInPlace(buf, fileKey, nonce);
                fs.Seek(r.Offset, SeekOrigin.Begin);
                fs.Write(buf, 0, buf.Length);
            }
            fs.Flush(true);
        }

        // 完整性用 RegionCrcs 校验（只覆盖被加密区），无需全文件 SHA-256 —— 这是保持大文件秒级的关键
        meta.OriginalSha256 = Array.Empty<byte>();

        // 用 PIN 封装 MK
        byte[] kek = V2Crypto.DeriveKek(pin, configSalt, CoreConstants.DefaultKdfIterations);
        byte[] emkBlob = V2Crypto.WrapMasterKey(kek, masterKey);

        byte[] metaPlain = meta.Serialize();
        byte[] metaCipher = new byte[metaPlain.Length + CoreConstants.GcmTagSize];
        byte[] metaNonce = V2Crypto.BlockNonce(fileNonce, ulong.MaxValue);
        using (var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize))
            gcm.Encrypt(metaNonce, metaPlain, metaCipher.AsSpan(0, metaPlain.Length),
                metaCipher.AsSpan(metaPlain.Length));

        // 3) 追加 footer
        using var outFs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None);
        outFs.Write(metaCipher);
        outFs.Write(BitConverter.GetBytes(metaCipher.Length));
        outFs.Write(configSalt);
        outFs.Write(fileSalt);
        outFs.Write(fileNonce);
        outFs.Write(emkBlob);
        outFs.WriteByte((byte)ProtectionMode.StructureLock);
        outFs.WriteByte(CoreConstants.V2Version);
        outFs.Write(CoreConstants.V2TailMagic);
        outFs.Flush(true);
    }

    /// <summary>读取尾部 footer 并解密元数据</summary>
    public static V2TailReadResult ReadFooter(string path, string? pin, string? recoveryCode)
    {
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        if (len < FixedTailSize) throw new InvalidDataException("File too small for v2 tail container.");

        // footer 各字段按"从文件末尾往前"的顺序分段读取：
        // [metaLen(4)][configSalt(16)][fileSalt(16)][fileNonce(12)][emkBlob(60)][mode(1)][ver(1)][magic(8)]
        byte[] tail = new byte[FixedTailSize];
        fs.Seek(len - FixedTailSize, SeekOrigin.Begin);
        fs.ReadExactly(tail, 0, tail.Length);

        // tail 是顺序读入的，字段在其中的偏移：
        // metaLen@0, configSalt@4, fileSalt@20, fileNonce@36, emkBlob@48, mode@108, ver@109, magic@110
        int magicPos = FixedTailSize - 8; // =110
        if (!tail.AsSpan(magicPos, 8).SequenceEqual(CoreConstants.V2TailMagic))
            throw new InvalidDataException("Not a v2 structure-lock tail container.");
        byte ver = tail[magicPos - 1];   // 109
        if (ver != CoreConstants.V2Version) throw new InvalidDataException($"Unsupported version {ver}");
        var mode = (ProtectionMode)tail[magicPos - 2]; // 108
        byte[] emkBlob = tail.AsSpan(48, CoreConstants.EmkBlobSize).ToArray();
        byte[] fileNonce = tail.AsSpan(36, CoreConstants.NonceSize).ToArray();
        byte[] fileSalt = tail.AsSpan(20, CoreConstants.SaltSize).ToArray();
        byte[] configSalt = tail.AsSpan(4, CoreConstants.SaltSize).ToArray();
        int metaLen = BitConverter.ToInt32(tail, 0);

        long metaCipherPos = len - FixedTailSize - metaLen;
        if (metaLen < CoreConstants.GcmTagSize || metaCipherPos < 0)
            throw new InvalidDataException("Corrupted tail metadata length.");
        byte[] metaCipher = new byte[metaLen];
        fs.Seek(metaCipherPos, SeekOrigin.Begin);
        fs.ReadExactly(metaCipher, 0, metaLen);

        byte[] masterKey;
        if (!string.IsNullOrEmpty(recoveryCode))
            masterKey = V2Crypto.RecoveryCodeToMasterKey(recoveryCode);
        else if (!string.IsNullOrEmpty(pin))
        {
            byte[] kek = V2Crypto.DeriveKek(pin, configSalt, CoreConstants.DefaultKdfIterations);
            masterKey = V2Crypto.UnwrapMasterKey(kek, emkBlob); // 错误 PIN → 认证异常
        }
        else throw new ArgumentException("PIN or recovery code is required.");

        byte[] fileKey = V2Crypto.DeriveFileKey(masterKey, fileSalt);
        byte[] metaNonce = V2Crypto.BlockNonce(fileNonce, ulong.MaxValue);
        byte[] metaPlain = new byte[metaLen - CoreConstants.GcmTagSize];
        using (var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize))
            gcm.Decrypt(metaNonce, metaCipher.AsSpan(0, metaPlain.Length),
                metaCipher.AsSpan(metaPlain.Length), metaPlain);

        var meta = V2Metadata.Deserialize(metaPlain);
        if (meta.Mode != mode) throw new InvalidDataException("Mode mismatch in tail container.");

        long bodyLength = metaCipherPos; // footer 之前都是原文件主体
        return new V2TailReadResult
        {
            Metadata = meta, FileKey = fileKey, FileNonce = fileNonce,
            BodyLength = bodyLength, FooterStart = metaCipherPos
        };
    }

    /// <summary>
    /// 原地解密各区、校验 CRC、截断 footer。返回是否全部校验通过。
    /// </summary>
    public static void RestoreInPlace(string path, V2TailReadResult read)
    {
        var meta = read.Metadata;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (int i = 0; i < meta.LockRegions.Count; i++)
            {
                var r = meta.LockRegions[i];
                byte[] buf = new byte[r.Length];
                fs.Seek(r.Offset, SeekOrigin.Begin);
                fs.ReadExactly(buf, 0, buf.Length);

                byte[] nonce = V2Crypto.BlockNonce(read.FileNonce, (ulong)r.Offset);
                CtrInPlace(buf, read.FileKey, nonce); // CTR 对称：再跑一遍即解密

                // CRC 校验（若存在）
                if (meta.RegionCrcs != null && i < meta.RegionCrcs.Count)
                {
                    uint crc = Crc32Helper.Compute(buf);
                    if (crc != meta.RegionCrcs[i])
                        throw new InvalidDataException($"Integrity check failed at region '{r.Kind}' (offset {r.Offset}).");
                }

                fs.Seek(r.Offset, SeekOrigin.Begin);
                fs.Write(buf, 0, buf.Length);
            }
            fs.Flush(true);
            fs.SetLength(read.BodyLength); // 截断尾部 footer
        }
    }

    private static void CtrInPlace(Span<byte> data, byte[] key, byte[] nonce)
    {
        byte[] counter = new byte[16];
        Buffer.BlockCopy(nonce, 0, counter, 0, Math.Min(nonce.Length, 12));
        byte[] ks = new byte[16];
        using var aes = Aes.Create();
        aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
        using var ecb = aes.CreateEncryptor();
        uint ord = 0;
        int blocks = data.Length / 16;
        for (int b = 0; b < blocks; b++)
        {
            counter[15] = (byte)ord; counter[14] = (byte)(ord >> 8);
            counter[13] = (byte)(ord >> 16); counter[12] = (byte)(ord >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = b * 16;
            for (int i = 0; i < 16; i++) data[off + i] ^= ks[i];
            ord++;
        }
        int rem = data.Length % 16;
        if (rem > 0)
        {
            counter[15] = (byte)ord; counter[14] = (byte)(ord >> 8);
            counter[13] = (byte)(ord >> 16); counter[12] = (byte)(ord >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = blocks * 16;
            for (int i = 0; i < rem; i++) data[off + i] ^= ks[i];
        }
    }
}

public sealed class V2TailReadResult
{
    public V2Metadata Metadata { get; set; } = new();
    public byte[] FileKey { get; set; } = Array.Empty<byte>();
    public byte[] FileNonce { get; set; } = Array.Empty<byte>();
    public long BodyLength { get; set; }
    public long FooterStart { get; set; }
}
