using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HideFiles.Core;

/// <summary>目录名混淆/还原的执行结果</summary>
public sealed class DirectoryRenameResult
{
    public string RootPath { get; set; } = string.Empty;
    public int Renamed { get; set; }
    public int Skipped { get; set; }
    public List<string> Failures { get; } = new();
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// 目录名可逆混淆：确定性映射，不依赖密钥，也不依赖任何外部索引文件。
///
/// 定位：只降低目录名的可读性（"防大部分人"级别）。本工具自身即可还原，
/// 不提供密码学保护；需要真正保密请用文件的两种保护模式。
///
/// 幂等：已带 `_hf_` 前缀的目录名会被跳过，重复执行不会二次混淆。
/// 作用范围：只处理给定目录**内部**的子目录（自最深一层往上改名），不改动给定目录自身，
/// 因此调用方拿到的路径始终有效。
/// </summary>
public static class DirectoryObfuscator
{
    private static readonly byte[] Mask = { 0x3F, 0x7A, 0x19, 0xB2, 0x5C, 0x8E, 0x4D, 0x01 };

    /// <summary>混淆后目录名的最大长度，避免撞 Windows 255 字符/路径上限</summary>
    private const int MaxObfuscatedNameLength = 200;

    public static bool IsObfuscatedName(string dirName) =>
        dirName.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>混淆单个目录名（已混淆的返回原值，保证幂等）</summary>
    public static string ObfuscateName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("目录名不能为空。", nameof(originalName));
        if (IsObfuscatedName(originalName)) return originalName;

        byte[] raw = Encoding.UTF8.GetBytes(originalName);
        byte[] masked = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            masked[i] = (byte)(raw[i] ^ MaskKey(i));

        string result = CoreConstants.ObfuscatedDirectoryPrefix + Base32.Encode(masked);
        if (result.Length > MaxObfuscatedNameLength)
            throw new InvalidOperationException(
                $"目录名过长，混淆后达到 {result.Length} 字符（上限 {MaxObfuscatedNameLength}）。请先缩短该目录名。");
        return result;
    }

    /// <summary>还原被混淆的目录名</summary>
    public static bool TryRestoreName(string obfuscatedName, out string restoredName)
    {
        restoredName = string.Empty;
        if (!IsObfuscatedName(obfuscatedName)) return false;

        string body = obfuscatedName.Substring(CoreConstants.ObfuscatedDirectoryPrefix.Length);
        if (body.Length == 0) return false;

        try
        {
            byte[] masked = Base32.DecodeVariableLength(body);
            if (masked.Length == 0) return false;

            byte[] raw = new byte[masked.Length];
            for (int i = 0; i < masked.Length; i++)
                raw[i] = (byte)(masked[i] ^ MaskKey(i));

            string name = Encoding.UTF8.GetString(raw);
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            restoredName = name;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>混淆给定目录内部的子目录名（自最深一层往上处理）</summary>
    public static DirectoryRenameResult ObfuscateDirectory(string rootPath, bool recursive = true)
        => Process(rootPath, recursive, obfuscate: true);

    /// <summary>还原给定目录内部被混淆的子目录名</summary>
    public static DirectoryRenameResult RestoreDirectory(string rootPath, bool recursive = true)
        => Process(rootPath, recursive, obfuscate: false);

    private static DirectoryRenameResult Process(string rootPath, bool recursive, bool obfuscate)
    {
        var result = new DirectoryRenameResult { RootPath = rootPath };
        if (!Directory.Exists(rootPath))
        {
            result.Failures.Add("目录不存在。");
            return result;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(rootPath, "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add($"无法枚举子目录：{ex.Message}");
            return result;
        }

        // 先改最深的目录：父目录改名后，已经处理过的子路径不会再用到
        Array.Sort(dirs, (a, b) => PathDepth(b).CompareTo(PathDepth(a)));

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string targetName;

            if (obfuscate)
            {
                if (IsObfuscatedName(name)) { result.Skipped++; continue; }
                try { targetName = ObfuscateName(name); }
                catch (Exception ex) { result.Failures.Add($"{name}: {ex.Message}"); continue; }
            }
            else
            {
                if (!IsObfuscatedName(name)) { result.Skipped++; continue; }
                if (!TryRestoreName(name, out targetName))
                {
                    result.Failures.Add($"{name}: 名称无法解码，已跳过。");
                    continue;
                }
            }

            string parent = Path.GetDirectoryName(dir) ?? rootPath;
            try
            {
                string dest = UniquePath.Resolve(parent, targetName);
                Directory.Move(dir, dest);
                result.Renamed++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{name}: {ex.Message}");
            }
        }

        return result;
    }

    private static byte MaskKey(int index) =>
        (byte)(Mask[index % Mask.Length] ^ ((index * 17 + 0x3C) & 0xFF));

    private static int PathDepth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);
}
