using System;
using System.IO;
using System.Text;

namespace HideFiles.Core;

/// <summary>
/// 通用名称（目录名/文件名）可逆混淆核心算法：
/// 确定性映射，不依赖密钥，不依赖外部索引文件。
///
/// 格式规范（v2）：
/// 前缀：_hf_
/// 载荷：Base32([0x02 格式标记][掩码后的原名 UTF-8][CRC32(原名) 低 16 位])
/// </summary>
public static class NameObfuscator
{
    private static readonly byte[] Mask = { 0x3F, 0x7A, 0x19, 0xB2, 0x5C, 0x8E, 0x4D, 0x01 };

    /// <summary>混淆后名称的最大长度，避免超 Windows 255 字符上限</summary>
    public const int MaxObfuscatedNameLength = 200;

    /// <summary>校验尾长度：CRC32 低 16 位</summary>
    private const int ChecksumSize = 2;

    /// <summary>v2 载荷格式标记字节</summary>
    private const byte FormatMarkerV2 = 0x02;

    /// <summary>判断名称是否具有混淆前缀</summary>
    public static bool IsObfuscated(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>混淆名称（已混淆则原样返回，保证幂等）</summary>
    public static string Obfuscate(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("名称不能为空。", nameof(originalName));
        if (IsObfuscated(originalName)) return originalName;

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
                $"名称过长，混淆后达到 {result.Length} 字符（上限 {MaxObfuscatedNameLength}）。请先缩短该名称。");
        return result;
    }

    /// <summary>还原被混淆的名称（v2 带校验尾；v1 旧格式尽力兼容）</summary>
    public static bool TryRestore(string obfuscatedName, out string restoredName)
    {
        restoredName = string.Empty;
        if (!IsObfuscated(obfuscatedName)) return false;

        string body = obfuscatedName.Substring(CoreConstants.ObfuscatedDirectoryPrefix.Length);
        if (body.Length == 0) return false;

        byte[]? payload = TryDecodeCanonical(body);
        if (payload == null || payload.Length == 0) return false;

        if (payload[0] == FormatMarkerV2)
        {
            // v2：必须有校验尾，且 CRC 必须匹配，否则直接拒绝
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

        // v1：无标记无校验尾，依赖严格解码与 UTF-8 校验
        byte[] legacyRaw = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            legacyRaw[i] = (byte)(payload[i] ^ MaskKey(i));
        return TryAcceptName(legacyRaw, out restoredName);
    }

    private static byte[]? TryDecodeCanonical(string body)
    {
        try
        {
            var (bytes, leftoverBits, leftoverValue) = Base32.DecodeDetailed(body);
            if (bytes.Length == 0) return null;
            if (leftoverBits > 0 && leftoverValue != 0) return null;
            return bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TryAcceptName(byte[] raw, out string name)
    {
        name = string.Empty;
        try
        {
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

    private static byte MaskKey(int index) =>
        (byte)(Mask[index % Mask.Length] ^ ((index * 17 + 0x3C) & 0xFF));
}
