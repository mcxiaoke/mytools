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
}

public sealed class ProcessFileResult
{
    public bool Success { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public double ElapsedMilliseconds { get; set; }
}

public sealed class FileObfuscator
{
    private static readonly byte[] ObfuscationKey = new byte[] { 0x5A, 0xC3, 0x96, 0x3C, 0xA5, 0x69, 0xF0, 0x0F };

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
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            info.FileSize = fs.Length;

            if (fs.Length < CoreConstants.MinFooterOverhead)
            {
                info.Status = FileStatus.Normal;
                return info;
            }

            fs.Seek(fs.Length - 8, SeekOrigin.Begin);
            byte[] marker = new byte[8];
            fs.ReadExactly(marker, 0, 8);

            bool isMarkerMatch = true;
            for (int i = 0; i < 8; i++)
            {
                if (marker[i] != CoreConstants.MagicMarker[i])
                {
                    isMarkerMatch = false;
                    break;
                }
            }

            if (!isMarkerMatch)
            {
                info.Status = FileStatus.Normal;
                return info;
            }

            // 是混淆文件，读取元数据
            var meta = MetadataBlock.ReadFromStream(fs);
            if (meta == null)
            {
                info.Status = FileStatus.Corrupted;
                info.ErrorMessage = "Corrupted obfuscation metadata.";
                return info;
            }

            info.Status = FileStatus.Obfuscated;
            info.OriginalFileName = meta.OriginalFileName;
            info.OriginalExtension = meta.OriginalExtension;
            info.ObfuscatedHeadLength = meta.ObfuscatedHeadLength;
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

    public ProcessFileResult ObfuscateFile(string filePath, HideFilesConfig config)
    {
        var sw = Stopwatch.StartNew();
        var res = new ProcessFileResult { OriginalPath = filePath, FinalPath = filePath };

        if (!File.Exists(filePath))
        {
            res.Success = false;
            res.ErrorMessage = "File not found.";
            return res;
        }

        try
        {
            var inspection = Inspect(filePath);
            if (inspection.Status == FileStatus.Obfuscated)
            {
                res.Success = false;
                res.ErrorMessage = "Already obfuscated (prevent duplicate obfuscation).";
                return res;
            }
            if (inspection.Status == FileStatus.Locked)
            {
                res.Success = false;
                res.ErrorMessage = "File is locked by another process.";
                return res;
            }

            string originalFileName = Path.GetFileName(filePath);
            string originalExt = Path.GetExtension(filePath);
            string dir = Path.GetDirectoryName(filePath)!;

            int headLimit = config.HeadObfuscateSize;

            byte[] originalHead;
            long origLen;
            uint headCrc;

            // 1. 就地读写头部与追加尾部
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                origLen = fs.Length;
                int actualHead = (int)Math.Min(headLimit, origLen);

                originalHead = new byte[actualHead];
                fs.Seek(0, SeekOrigin.Begin);
                if (actualHead > 0)
                {
                    fs.ReadExactly(originalHead, 0, actualHead);
                }

                headCrc = Crc32Helper.Compute(originalHead);

                // 生成混淆头数据
                byte[] obfuscatedHead = new byte[actualHead];
                for (int i = 0; i < actualHead; i++)
                {
                    byte k = ObfuscationKey[i % ObfuscationKey.Length];
                    obfuscatedHead[i] = (byte)(originalHead[i] ^ k ^ ((i * 31 + 0x5A) & 0xFF));
                }

                // 覆写头部
                fs.Seek(0, SeekOrigin.Begin);
                if (actualHead > 0)
                {
                    fs.Write(obfuscatedHead, 0, actualHead);
                }

                // 构造元数据
                var meta = new MetadataBlock
                {
                    OriginalFileName = originalFileName,
                    OriginalExtension = originalExt,
                    OriginalLength = origLen,
                    ObfuscatedHeadLength = actualHead,
                    HeadChecksum = headCrc,
                    OriginalHeadBytes = originalHead
                };

                byte[] metaPayload = meta.Serialize();

                // 追加到尾部
                fs.Seek(origLen, SeekOrigin.Begin);
                fs.Write(metaPayload, 0, metaPayload.Length);
                fs.Flush();
            }

            // 2. 根据规则改名与改扩展名
            config.MatchExtension(originalExt, out string fakeExt);
            string newFileName;
            if (config.ObfuscateFileName)
            {
                string stem = Path.GetFileNameWithoutExtension(originalFileName);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem + origLen))).Substring(0, 8).ToLowerInvariant();
                newFileName = $"hf_{hash}{fakeExt}";
            }
            else
            {
                string stem = Path.GetFileNameWithoutExtension(originalFileName);
                newFileName = $"{stem}{fakeExt}";
            }

            string targetPath = Path.Combine(dir, newFileName);

            // 若与原路径名不同，执行重命名
            if (!string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = EnsureUniquePath(targetPath);
                File.Move(filePath, targetPath);
            }

            res.Success = true;
            res.FinalPath = targetPath;
            sw.Stop();
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.ErrorMessage = ex.Message;
            sw.Stop();
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
    }

    public ProcessFileResult RestoreFile(string filePath)
    {
        var sw = Stopwatch.StartNew();
        var res = new ProcessFileResult { OriginalPath = filePath, FinalPath = filePath };

        if (!File.Exists(filePath))
        {
            res.Success = false;
            res.ErrorMessage = "File not found.";
            return res;
        }

        try
        {
            var inspection = Inspect(filePath);
            if (inspection.Status != FileStatus.Obfuscated)
            {
                res.Success = false;
                res.ErrorMessage = inspection.Status == FileStatus.Normal ? "File is not obfuscated." : (inspection.ErrorMessage ?? "Invalid file state.");
                return res;
            }

            string dir = Path.GetDirectoryName(filePath)!;
            string restoredName;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var meta = MetadataBlock.ReadFromStream(fs);
                if (meta == null)
                {
                    res.Success = false;
                    res.ErrorMessage = "Failed to parse metadata block.";
                    return res;
                }

                // 校验头部 CRC
                uint verifyCrc = Crc32Helper.Compute(meta.OriginalHeadBytes);
                if (verifyCrc != meta.HeadChecksum)
                {
                    res.Success = false;
                    res.ErrorMessage = "Original header CRC32 mismatch; file may be tampered.";
                    return res;
                }

                // 覆写还原头部
                fs.Seek(0, SeekOrigin.Begin);
                if (meta.ObfuscatedHeadLength > 0)
                {
                    fs.Write(meta.OriginalHeadBytes, 0, meta.ObfuscatedHeadLength);
                }

                // 截断尾部元数据
                fs.SetLength(meta.OriginalLength);
                fs.Flush();

                restoredName = meta.OriginalFileName;
            }

            // 恢复原文件名
            string targetPath = Path.Combine(dir, restoredName);
            if (!string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = EnsureUniquePath(targetPath);
                File.Move(filePath, targetPath);
            }

            res.Success = true;
            res.FinalPath = targetPath;
            sw.Stop();
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.ErrorMessage = ex.Message;
            sw.Stop();
            res.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        int counter = 1;
        while (true)
        {
            string candidate = Path.Combine(dir, $"{stem}_{counter}{ext}");
            if (!File.Exists(candidate)) return candidate;
            counter++;
        }
    }

    private static bool IsFileLocked(IOException exception)
    {
        int errorCode = exception.HResult & 0xFFFF;
        return errorCode == 32 || errorCode == 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
    }
}
