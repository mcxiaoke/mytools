using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HideFiles.Core;

/// <summary>文件名混淆/还原的执行结果</summary>
public sealed class FileRenameResult
{
    public string RootPath { get; set; } = string.Empty;
    public int Renamed { get; set; }
    public int Skipped { get; set; }
    public List<string> Failures { get; } = new();
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// 文件名可逆混淆：算法与目录名混淆一致（基于 NameObfuscator）。
/// 支持整名混淆（隐藏扩展名）与保留扩展名两种模式；
/// 还原时智能自适应识别，无需人工指定模式。
/// </summary>
public static class FileNameObfuscator
{
    private static readonly HashSet<string> IgnoredSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store"
    };

    /// <summary>
    /// 判断文件名是否已被混淆（整名以 _hf_ 开头，或主文件名以 _hf_ 开头）
    /// </summary>
    public static bool IsObfuscatedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (NameObfuscator.IsObfuscated(fileName)) return true;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return NameObfuscator.IsObfuscated(stem);
    }

    /// <summary>
    /// 混淆单个文件名。
    /// keepExtension=false 时对完整文件名（含后缀）整体混淆，彻底隐藏类型；
    /// keepExtension=true 时仅混淆主文件名，保留原扩展名。
    /// </summary>
    public static string ObfuscateFileName(string originalFileName, bool keepExtension = false)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
            throw new ArgumentException("文件名不能为空。", nameof(originalFileName));

        if (keepExtension)
        {
            string ext = Path.GetExtension(originalFileName);
            string stem = Path.GetFileNameWithoutExtension(originalFileName);
            if (string.IsNullOrEmpty(ext))
            {
                // 无扩展名，等同于整名混淆
                return NameObfuscator.Obfuscate(originalFileName);
            }
            if (NameObfuscator.IsObfuscated(stem)) return originalFileName;
            return NameObfuscator.Obfuscate(stem) + ext;
        }

        // 整名混淆
        if (NameObfuscator.IsObfuscated(originalFileName)) return originalFileName;
        return NameObfuscator.Obfuscate(originalFileName);
    }

    /// <summary>
    /// 还原被混淆的文件名。
    /// 自动识别整名混淆模式或保留扩展名模式，CRC 校验不通过直接拒绝。
    /// </summary>
    public static bool TryRestoreFileName(string obfuscatedFileName, out string restoredFileName)
    {
        restoredFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(obfuscatedFileName)) return false;

        // 1. 尝试整名还原（无扩展名或整体混淆）
        if (NameObfuscator.TryRestore(obfuscatedFileName, out restoredFileName))
            return true;

        // 2. 尝试保留扩展名还原（仅主文件名混淆）
        string ext = Path.GetExtension(obfuscatedFileName);
        if (!string.IsNullOrEmpty(ext))
        {
            string stem = Path.GetFileNameWithoutExtension(obfuscatedFileName);
            if (NameObfuscator.TryRestore(stem, out string restoredStem))
            {
                restoredFileName = restoredStem + ext;
                return true;
            }
        }

        return false;
    }

    /// <summary>混淆指定的文件列表</summary>
    public static FileRenameResult ObfuscateFiles(IEnumerable<string> filePaths, bool keepExtension = false)
    {
        var result = new FileRenameResult();
        foreach (string path in filePaths)
        {
            if (!File.Exists(path))
            {
                result.Failures.Add($"文件不存在：{path}");
                continue;
            }

            string name = Path.GetFileName(path);
            if (IgnoredSystemFiles.Contains(name))
            {
                result.Skipped++;
                continue;
            }

            if (keepExtension)
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                if (NameObfuscator.IsObfuscated(stem)) { result.Skipped++; continue; }
            }
            else
            {
                if (NameObfuscator.IsObfuscated(name)) { result.Skipped++; continue; }
            }

            string targetName;
            try
            {
                targetName = ObfuscateFileName(name, keepExtension);
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{name}: {ex.Message}");
                continue;
            }

            string parent = Path.GetDirectoryName(path) ?? string.Empty;
            try
            {
                string dest = UniquePath.Resolve(parent, targetName);
                File.Move(path, dest);
                result.Renamed++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{name}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>还原指定的文件列表</summary>
    public static FileRenameResult RestoreFiles(IEnumerable<string> filePaths)
    {
        var result = new FileRenameResult();
        foreach (string path in filePaths)
        {
            if (!File.Exists(path))
            {
                result.Failures.Add($"文件不存在：{path}");
                continue;
            }

            string name = Path.GetFileName(path);
            if (IgnoredSystemFiles.Contains(name))
            {
                result.Skipped++;
                continue;
            }

            if (!TryRestoreFileName(name, out string targetName))
            {
                result.Skipped++;
                continue;
            }

            string parent = Path.GetDirectoryName(path) ?? string.Empty;
            try
            {
                string dest = UniquePath.Resolve(parent, targetName);
                File.Move(path, dest);
                result.Renamed++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{name}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>混淆给定目录内部的所有文件名</summary>
    public static FileRenameResult ObfuscateDirectoryFiles(string rootPath, bool recursive = true, bool keepExtension = false)
    {
        var result = new FileRenameResult { RootPath = rootPath };
        if (!Directory.Exists(rootPath))
        {
            result.Failures.Add("目录不存在。");
            return result;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(rootPath, "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add($"无法枚举文件：{ex.Message}");
            return result;
        }

        var opResult = ObfuscateFiles(files, keepExtension);
        result.Renamed = opResult.Renamed;
        result.Skipped = opResult.Skipped;
        result.Failures.AddRange(opResult.Failures);
        return result;
    }

    /// <summary>还原给定目录内部所有被混淆的文件名</summary>
    public static FileRenameResult RestoreDirectoryFiles(string rootPath, bool recursive = true)
    {
        var result = new FileRenameResult { RootPath = rootPath };
        if (!Directory.Exists(rootPath))
        {
            result.Failures.Add("目录不存在。");
            return result;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(rootPath, "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add($"无法枚举文件：{ex.Message}");
            return result;
        }

        var opResult = RestoreFiles(files);
        result.Renamed = opResult.Renamed;
        result.Skipped = opResult.Skipped;
        result.Failures.AddRange(opResult.Failures);
        return result;
    }
}
