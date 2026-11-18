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
    private static readonly byte[] Mask = { 0x3F, 0x7A, 0x19, 0xB2, 0x5C, 0x8E, 0x4D, 0x01 };

    /// <summary>混淆后目录名的最大长度，避免撞 Windows 255 字符/路径上限</summary>
    private const int MaxObfuscatedNameLength = 200;

    /// <summary>校验尾长度：CRC32 低 16 位</summary>
    private const int ChecksumSize = 2;

    /// <summary>
    /// v2 载荷的格式标记字节（放在载荷首位，参与 Base32 编码）。
    /// 用载荷内标记而不是换前缀，是为了既保持 `_hf_` 对外形态不变（幂等判断、既有测试约定），
    /// 又能与 v1（无标记、无校验尾）无歧义区分。
    /// </summary>
    private const byte FormatMarkerV2 = 0x02;

    public static bool IsObfuscatedName(string dirName) =>
        dirName.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>混淆单个目录名（已混淆的返回原值，保证幂等）</summary>
    public static string ObfuscateName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("目录名不能为空。", nameof(originalName));
        if (IsObfuscatedName(originalName)) return originalName;

        byte[] raw = Encoding.UTF8.GetBytes(originalName);

        // 载荷 = [格式标记][掩码后的原名][CRC32 低 16 位]
        byte[] payload = new byte[1 + raw.Length + ChecksumSize];
        payload[0] = FormatMarkerV2;
        for (int i = 0; i < raw.Length; i++)
            payload[1 + i] = (byte)(raw[i] ^ MaskKey(i));

        ushort crc = (ushort)(Crc32Helper.Compute(raw) & 0xFFFF);
        payload[1 + raw.Length] = (byte)(crc >> 8);
        payload[2 + raw.Length] = (byte)crc;

        string result = CoreConstants.ObfuscatedDirectoryPrefix + Base32.Encode(payload);
        if (result.Length > MaxObfuscatedNameLength)
            throw new InvalidOperationException(
                $"目录名过长，混淆后达到 {result.Length} 字符（上限 {MaxObfuscatedNameLength}）。请先缩短该目录名。");
        return result;
    }

    /// <summary>还原被混淆的目录名（v2 带校验尾；v1 旧格式尽力兼容）</summary>
    public static bool TryRestoreName(string obfuscatedName, out string restoredName)
    {
        restoredName = string.Empty;
        if (!IsObfuscatedName(obfuscatedName)) return false;

        string body = obfuscatedName.Substring(CoreConstants.ObfuscatedDirectoryPrefix.Length);
        if (body.Length == 0) return false;

        byte[]? payload = TryDecodeCanonical(body);
        if (payload == null || payload.Length == 0) return false;

        if (payload[0] == FormatMarkerV2)
        {
            // v2：必须有校验尾，且 CRC 必须匹配，否则视为"不是本工具产生的名字"直接拒绝
            if (payload.Length < 1 + 1 + ChecksumSize) return false;
            int nameLen = payload.Length - 1 - ChecksumSize;
            byte[] raw = new byte[nameLen];
            for (int i = 0; i < nameLen; i++)
                raw[i] = (byte)(payload[1 + i] ^ MaskKey(i));

            ushort expected = (ushort)((payload[1 + nameLen] << 8) | payload[2 + nameLen]);
            ushort actual = (ushort)(Crc32Helper.Compute(raw) & 0xFFFF);
            if (expected != actual) return false;

            return TryAcceptName(raw, out restoredName);
        }

        // v1：无标记无校验尾，只能靠严格解码与严格 UTF-8 降低误判
        byte[] legacyRaw = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            legacyRaw[i] = (byte)(payload[i] ^ MaskKey(i));
        return TryAcceptName(legacyRaw, out restoredName);
    }

    /// <summary>
    /// Base32 严格解码：要求规范编码（尾部冗余位必须为 0）且长度足够。
    /// 规范编码天然排除掉绝大部分"随便起的" `_hf_xxx` 名字，是零成本的第一道过滤。
    /// </summary>
    private static byte[]? TryDecodeCanonical(string body)
    {
        try
        {
            var (bytes, leftoverBits, leftoverValue) = Base32.DecodeDetailed(body);
            if (bytes.Length == 0) return null;
            if (leftoverBits > 0 && leftoverValue != 0) return null;  // 非规范 Base32
            return bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>严格校验解码结果是否为合法 UTF-8 名称（拒绝替换字符与控制字符）</summary>
    private static bool TryAcceptName(byte[] raw, out string name)
    {
        name = string.Empty;
        try
        {
            // 严格 UTF-8：非法字节序列直接抛异常，而不是被替换成 U+FFFD
            name = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('\uFFFD')) return false;
        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        return true;
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
