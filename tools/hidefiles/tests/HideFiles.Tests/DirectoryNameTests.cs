using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 目录名混淆：可逆、幂等（不能重复应用）、冲突不覆盖、超长名明确报错。
/// </summary>
public class DirectoryNameTests : IDisposable
{
    private readonly string _testDir;

    public DirectoryNameTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    [Fact]
    public void ObfuscateName_IsIdempotent()
    {
        string once = DirectoryObfuscator.ObfuscateName("我的私密相册");
        string twice = DirectoryObfuscator.ObfuscateName(once);

        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, once);
        Assert.Equal(once, twice);      // 已混淆的目录名原样返回，不会二次混淆
    }

    [Fact]
    public void ObfuscateDirectory_SecondRun_RenamesNothing()
    {
        string root = Path.Combine(_testDir, "root");
        Directory.CreateDirectory(Path.Combine(root, "Photos", "2026"));

        var first = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(first.Success, string.Join(";", first.Failures));
        Assert.Equal(2, first.Renamed);
        Assert.Equal(0, first.Skipped);

        var second = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(second.Success, string.Join(";", second.Failures));
        Assert.Equal(0, second.Renamed);        // 幂等：第二次不做任何改名
        Assert.Equal(2, second.Skipped);

        Assert.Single(Directory.GetDirectories(root));
        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, Path.GetFileName(Directory.GetDirectories(root)[0]));
    }

    [Fact]
    public void ObfuscateDirectory_RoundTripsNestedTree()
    {
        string root = Path.Combine(_testDir, "tree");
        string deep = Path.Combine(root, "A", "B", "C");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "note.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "top.txt"), "top");

        var obf = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(obf.Success, string.Join(";", obf.Failures));
        Assert.Equal(3, obf.Renamed);
        Assert.False(Directory.Exists(Path.Combine(root, "A")));

        var allObfuscated = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        Assert.All(allObfuscated, d =>
            Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, Path.GetFileName(d)));

        var rest = DirectoryObfuscator.RestoreDirectory(root, recursive: true);
        Assert.True(rest.Success, string.Join(";", rest.Failures));
        Assert.Equal(3, rest.Renamed);

        Assert.True(Directory.Exists(deep));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(deep, "note.txt")));
        Assert.Equal("top", File.ReadAllText(Path.Combine(root, "top.txt")));
    }

    [Fact]
    public void RestoreDirectory_DoesNotOverwriteExistingName()
    {
        string root = Path.Combine(_testDir, "clash");
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "Alpha", "a.txt"), "original");

        var obf = DirectoryObfuscator.ObfuscateDirectory(root);
        Assert.True(obf.Success, string.Join(";", obf.Failures));

        // 用户之后又建了一个同名目录
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "Alpha", "a.txt"), "later");

        var rest = DirectoryObfuscator.RestoreDirectory(root);
        Assert.True(rest.Success, string.Join(";", rest.Failures));

        Assert.Equal("later", File.ReadAllText(Path.Combine(root, "Alpha", "a.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "Alpha (1)", "a.txt")));
    }

    [Fact]
    public void ObfuscateName_TooLong_Throws()
    {
        string longName = new string('目', 200);   // UTF-8 600 字节 → Base32 远超上限
        var ex = Assert.Throws<InvalidOperationException>(() => DirectoryObfuscator.ObfuscateName(longName));
        Assert.Contains("过长", ex.Message);
    }

    [Fact]
    public void RestoreName_RejectsNonObfuscatedAndBrokenNames()
    {
        Assert.False(DirectoryObfuscator.TryRestoreName("普通目录", out _));
        Assert.False(DirectoryObfuscator.TryRestoreName(CoreConstants.ObfuscatedDirectoryPrefix, out _));
        Assert.False(DirectoryObfuscator.TryRestoreName(CoreConstants.ObfuscatedDirectoryPrefix + "!!!!", out _));
    }

    /// <summary>
    /// 2.10 回归：用户自建、恰好以 `_hf_` 开头的目录不能被当成混淆名去还原，
    /// 否则会被改名成乱码（实测 `_hf_old` → `q`，原目录消失，属数据事故）。
    /// </summary>
    [Theory]
    [InlineData("_hf_old")]
    [InlineData("_hf_backup")]
    [InlineData("_hf_2026")]
    [InlineData("_hf_v2")]
    [InlineData("_hf_temp")]
    [InlineData("_hf_我的资料")]
    [InlineData("_hf_ABCDEF")]
    [InlineData("_hf_AAAA1111")]
    public void RestoreDirectory_DoesNotRenameUserOwnedPrefixedFolder(string userDirName)
    {
        string root = Path.Combine(_testDir, "root");
        Directory.CreateDirectory(root);
        string victim = Path.Combine(root, userDirName);
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "important");

        var result = DirectoryObfuscator.RestoreDirectory(root);

        Assert.True(Directory.Exists(victim), $"用户目录 {userDirName} 被误改名了");
        Assert.Equal(0, result.Renamed);
        Assert.Equal("important", File.ReadAllText(Path.Combine(victim, "keep.txt")));
    }

    /// <summary>v2 名字带 CRC 校验尾：篡改任意一个字符必须被拒绝，绝不改名</summary>
    [Fact]
    public void RestoreName_TamperedV2Name_IsRejected()
    {
        string obf = DirectoryObfuscator.ObfuscateName("项目资料");
        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, obf);

        // 断言正常往返
        Assert.True(DirectoryObfuscator.TryRestoreName(obf, out string ok));
        Assert.Equal("项目资料", ok);

        // 逐个字符篡改，必须全部被拒绝
        for (int i = CoreConstants.ObfuscatedDirectoryPrefix.Length; i < obf.Length; i++)
        {
            char[] chars = obf.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            string tampered = new string(chars);
            Assert.False(DirectoryObfuscator.TryRestoreName(tampered, out _),
                $"篡改第 {i} 位后仍被接受：{tampered}");
        }
    }

    /// <summary>
    /// 直接构造"格式标记正确、但 CRC 校验尾故意写错"的名字。
    /// 这是唯一能真正验证 CRC 校验生效的用例——仅靠篡改 Base32 字符会被
    /// 严格解码/UTF-8 检查提前挡掉，测不到校验尾本身。
    /// </summary>
    [Fact]
    public void RestoreName_V2WithWrongChecksum_IsRejected()
    {
        string forged = BuildV2NameWithBadChecksum("机密资料", wrongCrcDelta: 1);
        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, forged);
        Assert.False(DirectoryObfuscator.TryRestoreName(forged, out string restored),
            $"CRC 校验尾不匹配却被接受，还原结果：{restored}");
    }

    /// <summary>对照：校验尾正确的同构造名字必须能还原</summary>
    [Fact]
    public void RestoreName_V2WithCorrectChecksum_Restores()
    {
        string ok = BuildV2NameWithBadChecksum("机密资料", wrongCrcDelta: 0);
        Assert.True(DirectoryObfuscator.TryRestoreName(ok, out string restored));
        Assert.Equal("机密资料", restored);
    }

    /// <summary>按当前 v2 布局手工构造名字：载荷 = [0x02][掩码原名][CRC16]</summary>
    private static string BuildV2NameWithBadChecksum(string original, int wrongCrcDelta)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(original);
        byte[] payload = new byte[1 + raw.Length + 2];
        payload[0] = 0x02;
        for (int i = 0; i < raw.Length; i++)
            payload[1 + i] = (byte)(raw[i] ^ MaskKeyForTest(i));

        ushort crc = (ushort)((Crc32ForTest(raw) & 0xFFFF) + wrongCrcDelta);
        payload[1 + raw.Length] = (byte)(crc >> 8);
        payload[2 + raw.Length] = (byte)crc;

        return CoreConstants.ObfuscatedDirectoryPrefix + Base32ForTest.Encode(payload);
    }

    private static uint Crc32ForTest(byte[] bytes)
    {
        const uint poly = 0xEDB88320;
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>v1 旧格式（无标记、无校验尾）仍须可还原，保证升级后不丢历史目录</summary>
    [Fact]
    public void RestoreName_LegacyV1Format_StillRestores()
    {
        string legacy = BuildLegacyName("旧版目录");

        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, legacy);
        Assert.True(DirectoryObfuscator.TryRestoreName(legacy, out string restored),
            $"v1 名字无法还原：{legacy}");
        Assert.Equal("旧版目录", restored);
    }

    /// <summary>按 v1 算法（异或掩码后直接 Base32，无校验尾）构造旧格式名字</summary>
    private static string BuildLegacyName(string original)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(original);
        byte[] masked = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            masked[i] = (byte)(raw[i] ^ MaskKeyForTest(i));
        return CoreConstants.ObfuscatedDirectoryPrefix + Base32ForTest.Encode(masked);
    }

    private static byte MaskKeyForTest(int index)
    {
        byte[] mask = { 0x3F, 0x7A, 0x19, 0xB2, 0x5C, 0x8E, 0x4D, 0x01 };
        return (byte)(mask[index % mask.Length] ^ ((index * 17 + 0x3C) & 0xFF));
    }

    private static class Base32ForTest
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        public static string Encode(byte[] data)
        {
            var sb = new System.Text.StringBuilder();
            int bits = 0, value = 0;
            foreach (byte b in data)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
            return sb.ToString();
        }
    }
}
