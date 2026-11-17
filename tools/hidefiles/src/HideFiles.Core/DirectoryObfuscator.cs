using System;
using System.IO;
using System.Text;

namespace HideFiles.Core;

public static class DirectoryObfuscator
{
    private static readonly byte[] Salt = new byte[] { 0x3F, 0x7A, 0x19, 0xB2, 0x5C, 0x8E, 0x4D, 0x01 };

    /// <summary>
    /// 判断是否是已混淆的目录名
    /// </summary>
    public static bool IsObfuscatedName(string dirName)
    {
        return dirName.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 混淆单个目录名（可逆确定性生成）
    /// </summary>
    public static string ObfuscateName(string originalName)
    {
        if (IsObfuscatedName(originalName)) return originalName;

        byte[] raw = Encoding.UTF8.GetBytes(originalName);
        byte[] transformed = new byte[raw.Length];

        for (int i = 0; i < raw.Length; i++)
        {
            byte mask = (byte)(Salt[i % Salt.Length] ^ ((i * 17 + 0x3C) & 0xFF));
            transformed[i] = (byte)(raw[i] ^ mask);
        }

        return CoreConstants.ObfuscatedDirectoryPrefix + Convert.ToHexString(transformed).ToLowerInvariant();
    }

    /// <summary>
    /// 反解还原目录名
    /// </summary>
    public static bool TryRestoreName(string obfuscatedName, out string restoredName)
    {
        restoredName = string.Empty;
        if (!IsObfuscatedName(obfuscatedName)) return false;

        string hex = obfuscatedName.Substring(CoreConstants.ObfuscatedDirectoryPrefix.Length);
        try
        {
            byte[] raw = Convert.FromHexString(hex);
            byte[] restored = new byte[raw.Length];

            for (int i = 0; i < raw.Length; i++)
            {
                byte mask = (byte)(Salt[i % Salt.Length] ^ ((i * 17 + 0x3C) & 0xFF));
                restored[i] = (byte)(raw[i] ^ mask);
            }

            restoredName = Encoding.UTF8.GetString(restored);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
