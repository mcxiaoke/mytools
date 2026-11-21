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
///
/// 校验尾（v2 起）：编码尾部追加 2 字节校验（CRC32(原名) 低 16 位）并与名称一起做 Base32。
/// 还原时校验不通过即拒绝改名，从而把"仅本工具产生的名字才可还原"变成可验证事实——
/// 否则用户自有 `_hf_xxx` 目录会被误解码成乱码名，属数据事故（见改进建议 2.10）。
/// 无校验尾的旧格式（v1）仍可还原，但要求 Base32 规范编码 + 严格 UTF-8，尽量排除误判。
/// </summary>
public static class DirectoryObfuscator
{
    public static bool IsObfuscatedName(string dirName) => NameObfuscator.IsObfuscated(dirName);

    /// <summary>混淆单个目录名（已混淆的返回原值，保证幂等）</summary>
    public static string ObfuscateName(string originalName) => NameObfuscator.Obfuscate(originalName);

    /// <summary>还原被混淆的目录名（v2 带校验尾；v1 旧格式尽力兼容）</summary>
    public static bool TryRestoreName(string obfuscatedName, out string restoredName) =>
        NameObfuscator.TryRestore(obfuscatedName, out restoredName);

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

    private static int PathDepth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);
}
