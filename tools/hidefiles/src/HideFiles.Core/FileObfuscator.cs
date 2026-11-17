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
/// 混淆：把文件装入自包含 v2 容器（统一魔数，不暴露原始格式）。
///   - 结构锁（默认）：原地加密前 64KB 头部 + 格式关键结构，媒体主体明文，极速。
///   - 整体加密：全文件分块 AES-256-GCM，最强保护。
/// 还原：用 PIN / 恢复码 / 本机主密钥解开容器，结构锁校验各锁定区 CRC，整体加密校验 SHA-256。
/// </summary>
public sealed class FileObfuscator
{
    /// <summary>
    /// 检查文件状态。传入 masterKey（本机已解锁的主密钥）时会顺带解出原始文件名，
    /// 便于在列表/扫描结果里辨认被改名的容器；没有密钥时只判断容器类型。
    /// </summary>
    public FileInspection Inspect(string filePath, byte[]? masterKey = null)
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
            bool isFront = V2Container.IsV2File(filePath);
            bool isTail = !isFront && V2Tail.IsTailFile(filePath);
            if (!isFront && !isTail)
            {
                info.Status = FileStatus.Normal;
                return info;
            }
            info.Status = FileStatus.Obfuscated;
            if (isTail)
            {
                info.ProtectionMode = ProtectionMode.StructureLock;
            }
            else
            {
                // 只读头部字节判断模式标记（不解密）
                using var fs = File.OpenRead(filePath);
                byte[] head = new byte[CoreConstants.V2HeaderSize];
                fs.ReadExactly(head, 0, head.Length);
                info.ProtectionMode = (ProtectionMode)head[9];
            }

            TryFillMetadata(info, filePath, isTail, masterKey);
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

    /// <summary>有主密钥时解出元数据里的原始文件名；解不出就保持空，不影响状态判定</summary>
    private static void TryFillMetadata(FileInspection info, string path, bool isTail, byte[]? masterKey)
    {
        if (masterKey == null || masterKey.Length != CoreConstants.MasterKeySize) return;
        try
        {
            V2Metadata meta = isTail
                ? V2Tail.ReadFooter(path, null, null, masterKey).Metadata
                : V2Container.Read(path, null, null, masterKey).Metadata;
            info.OriginalFileName = meta.OriginalFileName;
            info.OriginalExtension = meta.OriginalExtension;
        }
        catch
        {
            // 不属于这批密钥的文件：保持字段为空
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
            if (V2Container.IsV2File(filePath) || V2Tail.IsTailFile(filePath))
            { res.ErrorMessage = "Already obfuscated."; return res; }

            byte[] masterKey = config.UnwrapMasterKeyWithPin(config.SessionPin);
            byte[] configSalt = Convert.FromBase64String(config.ConfigSalt!);

            string originalFileName = Path.GetFileName(filePath);
            string originalExt = Path.GetExtension(filePath);
            string dir = Path.GetDirectoryName(filePath)!;
            long origLen = new FileInfo(filePath).Length;

            var meta = new V2Metadata
            {
                OriginalFileName = originalFileName,
                OriginalExtension = originalExt,
                OriginalLength = origLen,
                Mode = config.ProtectionMode
            };

            string targetName = config.ObfuscateFileName
                ? $"hf_{BuildNameHash(originalFileName, origLen)}{GetFakeExtension(config, originalExt)}"
                : originalFileName;
            string targetPath = Path.Combine(dir, targetName);
            // 关闭文件名混淆（或哈希名恰好等于原名）时为原地处理，不需要改名
            bool renameRequired = !string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase);

            if (config.ProtectionMode == ProtectionMode.StructureLock)
            {
                // 结构锁：定位关键结构；找不到则直接报错，文件不动
                meta.LockRegions = StructureLocator.Locate(filePath, originalExt);
                if (meta.LockRegions.Count == 0)
                    throw new InvalidDataException(
                        $"结构锁模式无法识别该文件的关键结构（扩展名 {originalExt}）。" +
                        "请在设置中改用“整体加密”模式后重试，文件未被修改。");

                // 原地加密 + 尾部追加 footer：不整体重写，任意大小文件都快
                V2Tail.ProtectInPlace(filePath, meta, config.SessionPin, masterKey, configSalt,
                    config.HeadObfuscateSize);
                if (renameRequired)
                    targetPath = UniquePath.Move(filePath, dir, targetName);
            }
            else
            {
                // 整体加密：前置容器，全文件分块 GCM（必须重写）
                byte[] sha;
                using (var inFs = File.OpenRead(filePath))
                    sha = SHA256.HashData(inFs);
                meta.OriginalSha256 = sha;

                string tmpOut = Path.Combine(dir, $"hf_enc_{Guid.NewGuid():N}.tmp");
                var opt = new V2WriteOptions
                {
                    Mode = ProtectionMode.FullEncrypt,
                    MasterKey = masterKey,
                    ConfigSalt = configSalt,
                    KdfIterations = config.KdfIterations,
                    Pin = config.SessionPin
                };
                void WriteBody(Stream outFs, byte[] fileKey, byte[] fileNonce)
                {
                    using var inFs = File.OpenRead(filePath);
                    WriteFullEncryptBody(inFs, outFs, fileKey, fileNonce);
                }
                try
                {
                    V2Container.Write(tmpOut, meta, opt, WriteBody);

                    if (renameRequired)
                    {
                        // 先落地新名字，再删除原文；目标同名时自动改为 "原名 (1).ext"，绝不覆盖
                        targetPath = UniquePath.Move(tmpOut, dir, targetName);
                        File.Delete(filePath);
                    }
                    else
                    {
                        // 关闭文件名混淆：用容器原地替换原文件
                        File.Move(tmpOut, filePath, overwrite: true);
                        targetPath = filePath;
                    }
                }
                finally
                {
                    if (File.Exists(tmpOut)) { try { File.Delete(tmpOut); } catch { } }
                }
            }

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
    /// 还原文件。pin 与 recoveryCode 二选一；fallbackMasterKey 为本机 config 中当前 PIN 对应的
    /// 主密钥，用于"用户改过 PIN、文件内 emkBlob 仍是旧 PIN 封装"的情形。
    /// </summary>
    public ProcessFileResult RestoreFile(string filePath, string? pin, string? recoveryCode = null,
        byte[]? fallbackMasterKey = null)
    {
        var sw = Stopwatch.StartNew();
        var res = new ProcessFileResult { OriginalPath = filePath, FinalPath = filePath };
        try
        {
            if (!File.Exists(filePath)) { res.ErrorMessage = "File not found."; return res; }
            if (string.IsNullOrEmpty(pin) && string.IsNullOrEmpty(recoveryCode))
                throw new InvalidOperationException("还原需要 PIN 码或恢复码。");

            string dir = Path.GetDirectoryName(filePath)!;
            bool isTail = V2Tail.IsTailFile(filePath);

            if (isTail)
            {
                // 结构锁尾部容器：原地解密各区 + 校验 CRC + 截断 footer，不重写整体
                var read = V2Tail.ReadFooter(filePath, pin, recoveryCode, fallbackMasterKey);
                V2Tail.RestoreInPlace(filePath, read);

                string tailName = RequireOriginalName(read.Metadata);
                string tailTarget = Path.Combine(dir, tailName);
                if (string.Equals(filePath, tailTarget, StringComparison.OrdinalIgnoreCase))
                    tailTarget = filePath;                                  // 未改名的原地容器
                else
                    tailTarget = UniquePath.Move(filePath, dir, tailName);   // 同名已存在 → 原名 (1).ext

                sw.Stop();
                res.Success = true;
                res.FinalPath = tailTarget;
                res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
                return res;
            }

            // 前置容器（整体加密）：读出主体 + 校验
            var front = V2Container.Read(filePath, pin, recoveryCode, fallbackMasterKey);
            var meta = front.Metadata;
            string frontName = RequireOriginalName(meta);
            string tmpOut = Path.Combine(dir, $"hf_rst_{Guid.NewGuid():N}.tmp");
            string frontTarget;

            try
            {
                using (var inFs = File.OpenRead(filePath))
                using (var outFs = new FileStream(tmpOut, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    inFs.Seek(front.BodyOffset, SeekOrigin.Begin);
                    ReadFullEncryptBody(inFs, outFs, front.FileKey, front.FileNonce, meta.OriginalLength);
                }

                if (new FileInfo(tmpOut).Length != meta.OriginalLength)
                    throw new InvalidDataException("还原后文件长度不符。");
                byte[] restoredSha;
                using (var fs = File.OpenRead(tmpOut)) restoredSha = SHA256.HashData(fs);
                if (meta.OriginalSha256.Length > 0 &&
                    !CryptographicOperations.FixedTimeEquals(restoredSha, meta.OriginalSha256))
                    throw new InvalidDataException("还原后 SHA-256 校验失败，文件可能已损坏或被篡改。");

                if (string.Equals(filePath, Path.Combine(dir, frontName), StringComparison.OrdinalIgnoreCase))
                {
                    // 原始名与容器名相同（关闭文件名混淆）：用还原结果原地替换容器自身
                    File.Move(tmpOut, filePath, overwrite: true);
                    frontTarget = filePath;
                }
                else
                {
                    frontTarget = UniquePath.Move(tmpOut, dir, frontName);
                    File.Delete(filePath);
                }
            }
            finally
            {
                // 失败路径不得留下解密后的明文临时文件
                if (File.Exists(tmpOut)) { try { File.Delete(tmpOut); } catch { } }
            }

            sw.Stop();
            res.Success = true;
            res.FinalPath = frontTarget;
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

    // ==================== 整体加密主体 ====================
    private static void WriteFullEncryptBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce)
    {
        byte[] plain = new byte[CoreConstants.FullChunkSize];
        byte[] cipher = new byte[CoreConstants.FullChunkSize];
        byte[] tag = new byte[CoreConstants.GcmTagSize];
        byte[] lenBuf = new byte[4];
        ulong idx = 0;
        using var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize);

        while (true)
        {
            int n = inFs.Read(plain, 0, plain.Length);
            if (n <= 0) break;

            byte[] nonce = V2Crypto.BlockNonce(fileNonce, idx);
            gcm.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag);

            BitConverter.TryWriteBytes(lenBuf, n);    // 明文长度（末块可能不足 64KB）
            outFs.Write(lenBuf, 0, 4);
            outFs.Write(cipher, 0, n);
            outFs.Write(tag, 0, tag.Length);
            idx++;
        }
    }

    private static void ReadFullEncryptBody(Stream inFs, Stream outFs, byte[] fileKey, byte[] fileNonce, long originalLength)
    {
        byte[] lenBuf = new byte[4];
        byte[] cipher = new byte[CoreConstants.FullChunkSize];
        byte[] plain = new byte[CoreConstants.FullChunkSize];
        byte[] tag = new byte[CoreConstants.GcmTagSize];
        ulong idx = 0;
        long remaining = originalLength;
        using var gcm = new AesGcm(fileKey, CoreConstants.GcmTagSize);

        while (remaining > 0)
        {
            inFs.ReadExactly(lenBuf, 0, 4);
            int n = BitConverter.ToInt32(lenBuf);

            // 长度前缀不在 GCM 认证范围内，必须先做范围校验再分配/读取
            if (n <= 0 || n > CoreConstants.FullChunkSize)
                throw new InvalidDataException($"容器已损坏：分块长度异常（{n}）。");
            if (n > remaining)
                throw new InvalidDataException("容器已损坏：分块长度超出原始文件长度。");

            inFs.ReadExactly(cipher, 0, n);
            inFs.ReadExactly(tag, 0, tag.Length);

            byte[] nonce = V2Crypto.BlockNonce(fileNonce, idx);
            gcm.Decrypt(nonce, cipher.AsSpan(0, n), tag, plain.AsSpan(0, n));
            outFs.Write(plain, 0, n);
            remaining -= n;
            idx++;
        }
    }

    private static string GetFakeExtension(HideFilesConfig config, string originalExt)
    {
        if (config.MatchExtension(originalExt, out string fakeExt)) return fakeExt;
        return config.DefaultFakeExtension;
    }

    /// <summary>文件名混淆：主文件名 + 长度的 SHA-256 前 8 位十六进制</summary>
    private static string BuildNameHash(string fileName, long length)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stem + length));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// 取出元数据里的原始文件名。仅取文件名部分，剥离任何目录成分，
    /// 避免被篡改的元数据把文件写到目标目录之外。
    /// </summary>
    private static string RequireOriginalName(V2Metadata meta)
    {
        string name = Path.GetFileName(meta.OriginalFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("元数据缺少原始文件名，无法确定还原后的文件名。");
        return name;
    }

    private static bool IsFileLocked(IOException exception)
    {
        int errorCode = exception.HResult & 0xFFFF;
        return errorCode == 32 || errorCode == 33;
    }
}
