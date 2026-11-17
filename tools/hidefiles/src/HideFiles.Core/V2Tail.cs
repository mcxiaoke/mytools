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

    // footer 内部偏移（相对 footer 起始），BuildFooter 与 ReadFooter 共用，避免手写魔数错位
    private const int MetaLenOffset = 0;
    private const int ConfigSaltOffset = MetaLenOffset + 4;                          // 4
    private const int FileSaltOffset = ConfigSaltOffset + CoreConstants.SaltSize;    // 20
    private const int FileNonceOffset = FileSaltOffset + CoreConstants.SaltSize;     // 36
    private const int EmkBlobOffset = FileNonceOffset + CoreConstants.NonceSize;     // 48
    private const int ModeOffset = EmkBlobOffset + CoreConstants.EmkBlobSize;        // 108
    private const int VersionOffset = ModeOffset + 1;                                // 109
    private const int MagicOffset = VersionOffset + 1;                               // 110

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
    /// 失败安全：锁定区原始字节全程保留在内存（≤1MB，与文件大小无关），
    /// 任一步骤失败都会写回原样并截掉半截 footer，重试安全。
    /// </summary>
    public static void ProtectInPlace(string filePath, V2Metadata meta, string pin,
        byte[] masterKey, byte[] configSalt, int headSize = CoreConstants.DefaultHeadSize)
    {
        long origLen = new FileInfo(filePath).Length;
        meta.OriginalLength = origLen;
        meta.Mode = ProtectionMode.StructureLock;

        // 头部区 + 格式关键结构区，全部作为原地加密区
        var regions = new List<LockRegion>();
        int headLen = (int)Math.Min(Math.Clamp(headSize, CoreConstants.MinHeadSize, CoreConstants.MaxHeadSize), origLen);
        if (headLen > 0) regions.Add(new LockRegion(0, headLen, "HEADER"));
        foreach (var r in meta.LockRegions.OrderBy(r => r.Offset))
        {
            // 与头部区重叠的区域合并进头部区，只取最大覆盖范围（不能因后一个更短的区而回缩）
            if (r.Offset < headLen)
            {
                long end = Math.Max(regions[0].Length, r.Offset + r.Length);
                regions[0].Length = (int)Math.Min(end, origLen);
                continue;
            }
            regions.Add(new LockRegion(r.Offset, r.Length, r.Kind));
        }
        meta.LockRegions = regions;

        byte[] fileSalt = RandomNumberGenerator.GetBytes(CoreConstants.SaltSize);
        byte[] fileNonce = RandomNumberGenerator.GetBytes(CoreConstants.NonceSize);
        byte[] fileKey = V2Crypto.DeriveFileKey(masterKey, fileSalt);

        // 锁定区原始字节：既用于 CRC，也是失败回滚的唯一依据（必须在加密前留存副本）
        var originals = new List<byte[]>(regions.Count);
        meta.RegionCrcs = new List<uint>(regions.Count);

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                // 1) 读出全部锁定区原始字节
                foreach (var r in regions)
                {
                    byte[] buf = new byte[r.Length];
                    fs.Seek(r.Offset, SeekOrigin.Begin);
                    fs.ReadExactly(buf, 0, buf.Length);
                    originals.Add(buf);
                    meta.RegionCrcs.Add(Crc32Helper.Compute(buf));
                }

                // 2) 在副本上 CTR 加密后写回（原文件只在此处被改动）
                for (int i = 0; i < regions.Count; i++)
                {
                    byte[] cipher = (byte[])originals[i].Clone();
                    CtrInPlace(cipher, fileKey, V2Crypto.BlockNonce(fileNonce, (ulong)regions[i].Offset));
                    fs.Seek(regions[i].Offset, SeekOrigin.Begin);
                    fs.Write(cipher, 0, cipher.Length);
                }
                fs.Flush(true);

                // 3) 整段 footer 一次写入，缩小"半截 footer"的窗口
                byte[] footer = BuildFooter(meta, pin, masterKey, configSalt, fileSalt, fileNonce, fileKey);
                fs.Seek(0, SeekOrigin.End);
                fs.Write(footer, 0, footer.Length);
                fs.Flush(true);

                // 4) 提交点：长度与尾部魔数都对上才算成功
                EnsureCommitted(fs, origLen, footer.Length);
            }
            catch (Exception ex)
            {
                if (!TryRollback(fs, regions, originals, origLen))
                    throw new IOException($"结构锁写入失败，且回滚未能完成，文件可能损坏：{filePath}", ex);
                throw;
            }
        }
    }

    /// <summary>构造完整 footer（元数据 GCM 密文 + 密钥材料 + 版本/魔数）</summary>
    private static byte[] BuildFooter(V2Metadata meta, string pin, byte[] masterKey, byte[] configSalt,
        byte[] fileSalt, byte[] fileNonce, byte[] fileKey)
    {
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

        using var ms = new MemoryStream(FixedTailSize + metaCipher.Length);
        ms.Write(metaCipher);
        ms.Write(BitConverter.GetBytes(metaCipher.Length));
        ms.Write(configSalt);
        ms.Write(fileSalt);
        ms.Write(fileNonce);
        ms.Write(emkBlob);
        ms.WriteByte((byte)ProtectionMode.StructureLock);
        ms.WriteByte(CoreConstants.V2Version);
        ms.Write(CoreConstants.V2TailMagic);
        return ms.ToArray();
    }

    /// <summary>提交点判定：footer 长度与尾部魔数必须完全落盘</summary>
    private static void EnsureCommitted(FileStream fs, long originalLength, int footerLength)
    {
        long expected = originalLength + footerLength;
        if (fs.Length != expected)
            throw new InvalidDataException($"结构锁容器写入不完整（期望 {expected} 字节，实际 {fs.Length} 字节）。");

        fs.Seek(fs.Length - CoreConstants.V2TailMagic.Length, SeekOrigin.Begin);
        byte[] magic = new byte[CoreConstants.V2TailMagic.Length];
        fs.ReadExactly(magic, 0, magic.Length);
        if (!magic.AsSpan().SequenceEqual(CoreConstants.V2TailMagic))
            throw new InvalidDataException("结构锁容器尾部魔数校验失败。");
    }

    /// <summary>用内存中的原始字节回滚锁定区，并截掉可能已写入的 footer</summary>
    private static bool TryRollback(FileStream fs, List<LockRegion> regions, List<byte[]> originals, long originalLength)
    {
        try
        {
            for (int i = 0; i < originals.Count && i < regions.Count; i++)
            {
                if (originals[i].Length == 0) continue;
                fs.Seek(regions[i].Offset, SeekOrigin.Begin);
                fs.Write(originals[i], 0, originals[i].Length);
            }
            fs.SetLength(originalLength);
            fs.Flush(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取尾部 footer 并解密元数据（fallbackMasterKey 语义见 V2Crypto.ResolveMasterKey）</summary>
    public static V2TailReadResult ReadFooter(string path, string? pin, string? recoveryCode,
        byte[]? fallbackMasterKey = null)
    {
        using var fs = File.OpenRead(path);
        long len = fs.Length;
        if (len < FixedTailSize) throw new InvalidDataException("File too small for v2 tail container.");

        // footer 各字段按"从文件末尾往前"的顺序分段读取：
        // [metaLen(4)][configSalt(16)][fileSalt(16)][fileNonce(12)][emkBlob(60)][mode(1)][ver(1)][magic(8)]
        byte[] tail = new byte[FixedTailSize];
        fs.Seek(len - FixedTailSize, SeekOrigin.Begin);
        fs.ReadExactly(tail, 0, tail.Length);

        if (!tail.AsSpan(MagicOffset, 8).SequenceEqual(CoreConstants.V2TailMagic))
            throw new InvalidDataException("Not a v2 structure-lock tail container.");
        byte ver = tail[VersionOffset];
        if (ver != CoreConstants.V2Version) throw new InvalidDataException($"Unsupported version {ver}");
        var mode = (ProtectionMode)tail[ModeOffset];
        byte[] emkBlob = tail.AsSpan(EmkBlobOffset, CoreConstants.EmkBlobSize).ToArray();
        byte[] fileNonce = tail.AsSpan(FileNonceOffset, CoreConstants.NonceSize).ToArray();
        byte[] fileSalt = tail.AsSpan(FileSaltOffset, CoreConstants.SaltSize).ToArray();
        byte[] configSalt = tail.AsSpan(ConfigSaltOffset, CoreConstants.SaltSize).ToArray();
        int metaLen = BitConverter.ToInt32(tail, MetaLenOffset);

        long metaCipherPos = len - FixedTailSize - metaLen;
        if (metaLen < CoreConstants.GcmTagSize || metaCipherPos < 0)
            throw new InvalidDataException("Corrupted tail metadata length.");
        byte[] metaCipher = new byte[metaLen];
        fs.Seek(metaCipherPos, SeekOrigin.Begin);
        fs.ReadExactly(metaCipher, 0, metaLen);

        byte[] masterKey = V2Crypto.ResolveMasterKey(pin, recoveryCode, configSalt, emkBlob, fallbackMasterKey);

        byte[] fileKey = V2Crypto.DeriveFileKey(masterKey, fileSalt);
        byte[] metaNonce = V2Crypto.BlockNonce(fileNonce, ulong.MaxValue);
        byte[] metaPlain = new byte[metaLen - CoreConstants.GcmTagSize];
        try
        {
            using var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize);
            gcm.Decrypt(metaNonce, metaCipher.AsSpan(0, metaPlain.Length),
                metaCipher.AsSpan(metaPlain.Length), metaPlain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidDataException("PIN 或恢复码与该文件不匹配（元数据认证失败）。", ex);
        }

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
    /// 原地解密各区、校验 CRC、截断 footer。
    ///
    /// 两阶段执行：先把所有区读进内存并完成 CRC 校验，全部通过后才写回文件。
    /// 这样任何失败都不会让文件停在"部分区已解密"的状态，用户可以安全重试
    /// （CTR 自反，若中途写回后再重试会二次加密导致损坏）。
    /// </summary>
    public static void RestoreInPlace(string path, V2TailReadResult read)
    {
        var meta = read.Metadata;
        if (read.BodyLength != meta.OriginalLength)
            throw new InvalidDataException(
                $"容器长度与元数据不一致（body={read.BodyLength}, original={meta.OriginalLength}），文件可能被截断或拼接。");

        var regions = meta.LockRegions;
        var plains = new List<byte[]>(regions.Count);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // 阶段一：全部读出、解密、校验（不改动文件）
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            byte[] buf = new byte[r.Length];
            fs.Seek(r.Offset, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, buf.Length);

            CtrInPlace(buf, read.FileKey, V2Crypto.BlockNonce(read.FileNonce, (ulong)r.Offset)); // CTR 对称：再跑一遍即解密

            if (meta.RegionCrcs != null && i < meta.RegionCrcs.Count)
            {
                uint crc = Crc32Helper.Compute(buf);
                if (crc != meta.RegionCrcs[i])
                    throw new InvalidDataException($"Integrity check failed at region '{r.Kind}' (offset {r.Offset}).");
            }
            plains.Add(buf);
        }

        // 阶段二：全部校验通过后才写回并截断 footer
        for (int i = 0; i < regions.Count; i++)
        {
            if (plains[i].Length == 0) continue;
            fs.Seek(regions[i].Offset, SeekOrigin.Begin);
            fs.Write(plains[i], 0, plains[i].Length);
        }
        fs.Flush(true);
        fs.SetLength(read.BodyLength); // 截断尾部 footer
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
