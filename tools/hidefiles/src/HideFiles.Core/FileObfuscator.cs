using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HideFiles.Core;

public enum FileStatus
{
    Normal = 0,
    Obfuscated = 1,
    Corrupted = 2,
    Locked = 3,
    NotFound = 4
}

public sealed class FileInspection
{
    public string FilePath { get; set; } = string.Empty;
    public FileStatus Status { get; set; }
    public long FileSize { get; set; }
    public string? OriginalFileName { get; set; }
    public string? OriginalExtension { get; set; }
    public int? ObfuscatedHeadLength { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>v2 容器使用的保护模式</summary>
    public ProtectionMode? ProtectionMode { get; set; }
}

public sealed class ProcessFileResult
{
    public bool Success { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public double ElapsedMilliseconds { get; set; }
}

/// <summary>
/// v2 文件混淆/还原引擎。
/// 混淆：把整个原始文件装入自包含 v2 容器（统一魔数，不泄露源格式）。
///   - 默认结构锁模式：仅加密格式关键结构（mp4/mkv/jpg/png 定位），媒体主体明文，极速。
///   - 可选整体加密模式：全文件分块 AES-256-GCM，最强保护。
/// 还原：PIN 或恢复码解密容器，校验原 SHA-256，逐字节无损还原。
/// </summary>
public sealed class FileObfuscator
{
    public FileInspection Inspect(string filePath)
    {
        var info = new FileInspection { FilePath = filePath };
        if (!File.Exists(filePath))
        {
            info.Status = FileStatus.NotFound;
            info.ErrorMessage = "File does not exist.";
            return info;
        }
        try
        {
            info.FileSize = new FileInfo(filePath).Length;
            if (!V2Container.IsV2File(filePath))
            {
                info.Status = FileStatus.Normal;
                return info;
            }
            // 只读取头部元数据的模式标记用于展示（不解密）
            using var fs = File.OpenRead(filePath);
            byte[] head = new byte[CoreConstants.V2HeaderSize];
            fs.ReadExactly(head, 0, head.Length);
            info.Status = FileStatus.Obfuscated;
            info.ProtectionMode = (ProtectionMode)head[9];
            info.OriginalExtension = info.ProtectionMode == ProtectionMode.StructureLock
                ? "结构锁" : "整体加密";
            return info;
        }
        catch (IOException ex) when (IsFileLocked(ex))
        {
            info.Status = FileStatus.Locked;
            info.ErrorMessage = "File is locked by another process.";
            return info;
        }
        catch (Exception ex)
        {
            info.Status = FileStatus.Corrupted;
            info.ErrorMessage = ex.Message;
            return info;
        }
    }

    /// <summary>
    /// 混淆文件到 v2 容器。要求 config 已设置 PIN（config.SessionPin 有效）。
    /// </summary>
    public ProcessFileResult ObfuscateFile(string filePath, HideFilesConfig config)
    {
        var sw = Stopwatch.StartNew();
        var res = new ProcessFileResult { OriginalPath = filePath, FinalPath = filePath };
        try
        {
            if (!File.Exists(filePath)) { res.ErrorMessage = "File not found."; return res; }
            if (string.IsNullOrEmpty(config.SessionPin))
                throw new InvalidOperationException("请先设置 PIN 码（v2 加密需要 PIN 保护主密钥）。");
            if (string.IsNullOrEmpty(config.ConfigSalt) || string.IsNullOrEmpty(config.WrappedMasterKey))
                throw new InvalidOperationException("密钥材料未初始化，请重新设置 PIN。");
            if (V2Container.IsV2File(filePath))
            { res.ErrorMessage = "Already obfuscated."; return res; }

            byte[] masterKey = config.UnwrapMasterKeyWithPin(config.SessionPin);
            byte[] configSalt = Convert.FromBase64String(config.ConfigSalt!);

            string originalFileName = Path.GetFileName(filePath);
            string originalExt = Path.GetExtension(filePath);
            string dir = Path.GetDirectoryName(filePath)!;
            long origLen = new FileInfo(filePath).Length;

            // 原文件 SHA-256（整体读取，用于还原后校验）
            byte[] sha;
            using (var inFs = File.OpenRead(filePath))
                sha = SHA256.HashData(inFs);

            var meta = new V2Metadata
            {
                OriginalFileName = originalFileName,
                OriginalExtension = originalExt,
                OriginalLength = origLen,
                OriginalSha256 = sha,
                Mode = config.ProtectionMode
            };

            // 结构锁：定位关键结构；找不到则直接报错，交由用户显式改用整体加密模式
            ProtectionMode effectiveMode = config.ProtectionMode;
            if (effectiveMode == ProtectionMode.StructureLock)
            {
                meta.LockRegions = StructureLocator.Locate(filePath, originalExt);
                if (meta.LockRegions.Count == 0)
                    throw new InvalidDataException(
                        $"结构锁模式无法识别该文件的关键结构（扩展名 {originalExt}）。" +
                        "请在设置中改用“整体加密”模式后重试，文件未被修改。");
            }

            string baseName = Path.GetFileNameWithoutExtension(originalFileName);
            string fakeExt = config.ObfuscateFileName
                ? GetFakeExtension(config, originalExt)
                : originalExt;
            string newName = config.ObfuscateFileName
                ? $"hf_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseName + origLen))).Substring(0, 8).ToLowerInvariant()}{fakeExt}"
                : $"{baseName}{fakeExt}";
            string targetPath = Path.Combine(dir, newName);
            if (string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
                targetPath = Path.Combine(dir, $"hf_{Guid.NewGuid():N}{fakeExt}");

            // 生成新文件到临时路径，成功后替换原文件
            string tmpOut = targetPath + ".hfv2tmp";
            var opt = new V2WriteOptions
            {
                Mode = effectiveMode,
                MasterKey = masterKey,
                ConfigSalt = configSalt,
                KdfIterations = config.KdfIterations,
                Pin = config.SessionPin
            };
            meta.Mode = effectiveMode;

            void WriteBody(Stream outFs, byte[] fileKey, byte[] fileNonce)
            {
                using var inFs = File.OpenRead(filePath);
                if (effectiveMode == ProtectionMode.StructureLock)
                    WriteStructureLockBody(inFs, outFs, fileKey, fileNonce, meta.LockRegions);
                else
                    WriteFullEncryptBody(inFs, outFs, fileKey, fileNonce);
            }

            V2Container.Write(filePath, tmpOut, meta, opt, WriteBody);
            File.Move(tmpOut, targetPath, overwrite: true);
            File.Delete(filePath);

            sw.Stop();
            res.Success = true;
            res.FinalPath = targetPath;
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            res.ErrorMessage = ex.Message;
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
    }

    /// <summary>
    /// 还原文件。pin 与 recoveryCode 二选一。
    /// </summary>
    public ProcessFileResult RestoreFile(string filePath, string? pin, string? recoveryCode = null)
    {
        var sw = Stopwatch.StartNew();
        var res = new ProcessFileResult { OriginalPath = filePath, FinalPath = filePath };
        try
        {
            if (!File.Exists(filePath)) { res.ErrorMessage = "File not found."; return res; }
            if (string.IsNullOrEmpty(pin) && string.IsNullOrEmpty(recoveryCode))
                throw new InvalidOperationException("还原需要 PIN 码或恢复码。");

            var read = V2Container.Read(filePath, pin, recoveryCode);
            var meta = read.Metadata;

            string dir = Path.GetDirectoryName(filePath)!;
            string targetPath = Path.Combine(dir, meta.OriginalFileName);
            string tmpOut = targetPath + ".hfrstmp";

            using (var inFs = File.OpenRead(filePath))
            using (var outFs = new FileStream(tmpOut, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                inFs.Seek(read.BodyOffset, SeekOrigin.Begin);
                if (meta.Mode == ProtectionMode.StructureLock)
                    ReadStructureLockBody(inFs, outFs, read.FileKey, read.FileNonce, meta.LockRegions, meta.OriginalLength);
                else
                    ReadFullEncryptBody(inFs, outFs, read.FileKey, read.FileNonce, meta.OriginalLength);
            }

            // 还原后长度与 SHA-256 校验
            if (new FileInfo(tmpOut).Length != meta.OriginalLength)
                throw new InvalidDataException("还原后文件长度不符。");
            byte[] restoredSha;
            using (var fs = File.OpenRead(tmpOut)) restoredSha = SHA256.HashData(fs);
            if (meta.OriginalSha256.Length > 0 &&
                !CryptographicOperations.FixedTimeEquals(restoredSha, meta.OriginalSha256))
                throw new InvalidDataException("还原后 SHA-256 校验失败，文件可能已损坏或被篡改。");

            File.Move(tmpOut, targetPath, overwrite: true);
            File.Delete(filePath);

            sw.Stop();
            res.Success = true;
            res.FinalPath = targetPath;
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            res.ErrorMessage = ex.Message;
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
    }

    // ==================== 结构锁主体 ====================
    private static void WriteStructureLockBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce, List<LockRegion> regions)
    {
        long pos = 0;
        long len = inFs.Length;
        byte[] buf = new byte[64 * 1024];
        var sorted = regions.OrderBy(r => r.Offset).ToList();
        int ri = 0;
        while (pos < len)
        {
            int want = (int)Math.Min(buf.Length, len - pos);
            inFs.Seek(pos, SeekOrigin.Begin);
            inFs.ReadExactly(buf, 0, want);

            // 对落在当前窗口内的锁定区做原地 GCM 加密（每区独立 nonce）
            while (ri < sorted.Count && sorted[ri].Offset + sorted[ri].Length <= pos)
                ri++;
            for (int k = ri; k < sorted.Count; k++)
            {
                var r = sorted[k];
                if (r.Offset >= pos + want) break;
                long start = Math.Max(r.Offset, pos);
                long end = Math.Min(r.Offset + r.Length, pos + want);
                if (end <= start) continue;
                int off = (int)(start - pos);
                int cnt = (int)(end - start);
                byte[] nonce = V2Crypto.BlockNonce(fileNonce, (ulong)r.Offset);
                EncryptInPlace(buf.AsSpan(off, cnt), fileKey, nonce);
            }
            outFs.Write(buf, 0, want);
            pos += want;
        }
    }

    private static void ReadStructureLockBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce,
        List<LockRegion> regions, long bodyLength)
    {
        // 结构锁不改变长度，按偏移逐区解密后流式拷贝
        long pos = 0;
        long len = bodyLength;
        byte[] buf = new byte[64 * 1024];
        var sorted = regions.OrderBy(r => r.Offset).ToList();
        int ri = 0;
        while (pos < len)
        {
            int want = (int)Math.Min(buf.Length, len - pos);
            inFs.ReadExactly(buf, 0, want);
            while (ri < sorted.Count && sorted[ri].Offset + sorted[ri].Length <= pos) ri++;
            for (int k = ri; k < sorted.Count; k++)
            {
                var r = sorted[k];
                if (r.Offset >= pos + want) break;
                long start = Math.Max(r.Offset, pos);
                long end = Math.Min(r.Offset + r.Length, pos + want);
                if (end <= start) continue;
                int off = (int)(start - pos);
                int cnt = (int)(end - start);
                byte[] nonce = V2Crypto.BlockNonce(fileNonce, (ulong)r.Offset);
                DecryptInPlace(buf.AsSpan(off, cnt), fileKey, nonce);
            }
            outFs.Write(buf, 0, want);
            pos += want;
        }
    }

    // ==================== 整体加密主体 ====================
    private static void WriteFullEncryptBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce)
    {
        byte[] plain = new byte[CoreConstants.FullChunkSize];
        ulong idx = 0;
        while (true)
        {
            int n = inFs.Read(plain, 0, plain.Length);
            if (n == 0) break;
            byte[] nonce = V2Crypto.BlockNonce(fileNonce, idx);
            byte[] tag = new byte[CoreConstants.GcmTagSize];
            byte[] cipher = new byte[n];
            using (var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize))
                gcm.Encrypt(nonce, plain.AsSpan(0, n), cipher, tag);
            outFs.Write(BitConverter.GetBytes(n));   // 明文长度（末块）
            outFs.Write(cipher);
            outFs.Write(tag);
            idx++;
        }
    }

    private static void ReadFullEncryptBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce, long originalLength)
    {
        byte[] lenBuf = new byte[4];
        ulong idx = 0;
        long written = 0;
        while (written < originalLength)
        {
            inFs.ReadExactly(lenBuf, 0, 4);
            int n = BitConverter.ToInt32(lenBuf);
            byte[] cipher = new byte[n];
            byte[] tag = new byte[CoreConstants.GcmTagSize];
            inFs.ReadExactly(cipher, 0, n);
            inFs.ReadExactly(tag, 0, tag.Length);
            byte[] nonce = V2Crypto.BlockNonce(fileNonce, idx);
            byte[] plain = new byte[n];
            using (var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize))
                gcm.Decrypt(nonce, cipher, tag, plain);
            outFs.Write(plain);
            written += n;
            idx++;
        }
    }

    // ==================== 原地流式加解密（CTR 模式，保证长度不变）====================
    private static void EncryptInPlace(Span<byte> data, byte[] key, byte[] nonce)
        => CtrInPlace(data, key, nonce);
    private static void DecryptInPlace(Span<byte> data, byte[] key, byte[] nonce)
        => CtrInPlace(data, key, nonce); // CTR 对称

    private static void CtrInPlace(Span<byte> data, byte[] key, byte[] nonce)
    {
        // 结构锁要求长度不变，故用 AES-CTR（不是 GCM，避免附加 tag 改变长度）。
        // 完整性由元数据里的原文件 SHA-256 在还原时统一校验。
        // counter block = 16 字节：nonce(12) || 4 字节大端块序号（从 0 开始）
        byte[] counter = new byte[16];
        Buffer.BlockCopy(nonce, 0, counter, 0, Math.Min(nonce.Length, 12));
        byte[] ks = new byte[16];
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var ecb = aes.CreateEncryptor();
        uint blockOrdinal = 0;
        int blocks = data.Length / 16;
        for (int b = 0; b < blocks; b++)
        {
            counter[15] = (byte)blockOrdinal;
            counter[14] = (byte)(blockOrdinal >> 8);
            counter[13] = (byte)(blockOrdinal >> 16);
            counter[12] = (byte)(blockOrdinal >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = b * 16;
            for (int i = 0; i < 16; i++) data[off + i] ^= ks[i];
            blockOrdinal++;
        }
        int rem = data.Length % 16;
        if (rem > 0)
        {
            counter[15] = (byte)blockOrdinal;
            counter[14] = (byte)(blockOrdinal >> 8);
            counter[13] = (byte)(blockOrdinal >> 16);
            counter[12] = (byte)(blockOrdinal >> 24);
            ecb.TransformBlock(counter, 0, 16, ks, 0);
            int off = blocks * 16;
            for (int i = 0; i < rem; i++) data[off + i] ^= ks[i];
        }
    }

    private static string GetFakeExtension(HideFilesConfig config, string originalExt)
    {
        if (config.MatchExtension(originalExt, out string fakeExt)) return fakeExt;
        return config.DefaultFakeExtension;
    }

    private static bool IsFileLocked(IOException exception)
    {
        int errorCode = exception.HResult & 0xFFFF;
        return errorCode == 32 || errorCode == 33;
    }
}
