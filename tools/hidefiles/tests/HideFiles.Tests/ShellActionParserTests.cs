using System.Collections.Generic;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 右键动词 → 保护模式的映射回归。
/// 2026-11-18 的 P0 回归：`--unhide` 与 `--decrypt` 都被硬编码为 StructureLock，
/// 导致右键"整体解密"一个整体加密文件必然失败。
/// </summary>
public class ShellActionParserTests
{
    [Theory]
    [InlineData("--hide", ProtectionMode.StructureLock, true)]
    [InlineData("-hide", ProtectionMode.StructureLock, true)]
    [InlineData("--encrypt", ProtectionMode.FullEncrypt, true)]
    [InlineData("-encrypt", ProtectionMode.FullEncrypt, true)]
    [InlineData("--unhide", ProtectionMode.StructureLock, false)]
    [InlineData("-unhide", ProtectionMode.StructureLock, false)]
    [InlineData("--decrypt", ProtectionMode.FullEncrypt, false)]
    [InlineData("-decrypt", ProtectionMode.FullEncrypt, false)]
    public void Verb_MapsToExpectedModeAndDirection(string verb, ProtectionMode expectedMode, bool expectedObfuscate)
    {
        var req = ShellActionParser.Parse(new[] { verb, @"C:\tmp\a.mp4" });

        Assert.True(req.IsAutoProcess);
        Assert.Equal(expectedMode, req.Mode);
        Assert.Equal(expectedObfuscate, req.IsObfuscate);
        Assert.Single(req.Paths);
    }

    /// <summary>核心断言：--decrypt 绝不能退化成结构加密模式</summary>
    [Fact]
    public void DecryptVerb_MustNotUseStructureLock()
    {
        var req = ShellActionParser.Parse(new[] { "--decrypt", @"C:\tmp\encrypted.dat" });
        Assert.Equal(ProtectionMode.FullEncrypt, req.Mode);
    }

    /// <summary>核心断言：--encrypt 与 --decrypt 必须使用同一模式（否则必然出现一 encrypt 就解不开）</summary>
    [Theory]
    [InlineData("--hide", "--unhide")]
    [InlineData("--encrypt", "--decrypt")]
    public void EncryptAndDecryptVerbs_UseSameMode(string encryptVerb, string decryptVerb)
    {
        var enc = ShellActionParser.Parse(new[] { encryptVerb, "x" });
        var dec = ShellActionParser.Parse(new[] { decryptVerb, "x" });
        Assert.Equal(enc.Mode, dec.Mode);
    }

    [Fact]
    public void DecryptVerb_RoutesToSecureWorkspace()
    {
        // AutoProcessPathsAsync 用 Mode 选集合：FullEncrypt → "secure"
        var req = ShellActionParser.Parse(new[] { "--decrypt", "x" });
        Assert.Equal(ProtectionMode.FullEncrypt, req.Mode);
    }

    [Fact]
    public void OpenVerb_OnlyLoadsPaths_NoAutoProcess()
    {
        var req = ShellActionParser.Parse(new[] { "--open", @"C:\a.mp4", @"C:\b.png" });

        Assert.Equal(ShellActionKind.Open, req.Kind);
        Assert.False(req.IsAutoProcess);
        Assert.Null(req.Mode);
        Assert.Null(req.IsObfuscate);
        Assert.Equal(2, req.Paths.Count);
    }

    [Fact]
    public void NoVerb_DefaultsToOpen_SoDroppedPathsAreNotAutoProcessed()
    {
        var req = ShellActionParser.Parse(new[] { @"C:\a.mp4" });
        Assert.Equal(ShellActionKind.Open, req.Kind);
        Assert.False(req.IsAutoProcess);
        Assert.Single(req.Paths);
    }

    [Fact]
    public void UnknownSwitches_AreIgnored_PathsPreserved()
    {
        var req = ShellActionParser.Parse(new[] { "--unknown", @"C:\a.mp4" });
        Assert.Equal(ShellActionKind.Open, req.Kind);
        Assert.Single(req.Paths);
        Assert.Equal(@"C:\a.mp4", req.Paths[0]);
    }

    /// <summary>多选文件：所有路径都要保留（右键多选会传多条路径）</summary>
    [Fact]
    public void MultiplePaths_AllPreserved()
    {
        var req = ShellActionParser.Parse(new[] { "--decrypt", @"C:\1.dat", @"C:\2.dat", @"C:\3.dat" });
        Assert.Equal(3, req.Paths.Count);
        Assert.Equal(ProtectionMode.FullEncrypt, req.Mode);
    }

    /// <summary>动词在路径之后也要能识别（不同资源管理器/调用方式顺序可能不同）</summary>
    [Fact]
    public void VerbAfterPaths_StillRecognized()
    {
        var req = ShellActionParser.Parse(new[] { @"C:\a.dat", "--decrypt" });
        Assert.Equal(ShellActionKind.Decrypt, req.Kind);
        Assert.Single(req.Paths);
    }

    [Fact]
    public void EmptyArgs_ProducesOpenWithNoPaths()
    {
        var req = ShellActionParser.Parse(new List<string>());
        Assert.Equal(ShellActionKind.Open, req.Kind);
        Assert.Empty(req.Paths);
        Assert.False(req.IsAutoProcess);
    }
    // ---------- 新右键菜单：合并动词 ----------

    /// <summary>--obfuscate 属于混淆工作台，方向需按容器自动判定（IsObfuscate = null）</summary>
    [Theory]
    [InlineData("--obfuscate")]
    [InlineData("-obfuscate")]
    public void ObfuscateSmartVerb_PicksStructureWorkspace_AutoDirection(string verb)
    {
        var req = ShellActionParser.Parse(new[] { verb, @"C:	mp.mp4" });

        Assert.Equal(ShellActionKind.ObfuscateSmart, req.Kind);
        Assert.True(req.IsAutoProcess);
        Assert.Equal(ProtectionMode.StructureLock, req.Mode);
        Assert.Null(req.IsObfuscate);       // null = 逐文件判定方向
        Assert.Single(req.Paths);
    }

    /// <summary>--secure 属于加密工作台，方向同样自动判定</summary>
    [Theory]
    [InlineData("--secure")]
    [InlineData("-secure")]
    public void SecureSmartVerb_PicksEncryptWorkspace_AutoDirection(string verb)
    {
        var req = ShellActionParser.Parse(new[] { verb, @"C:	mp.bin" });

        Assert.Equal(ShellActionKind.SecureSmart, req.Kind);
        Assert.True(req.IsAutoProcess);
        Assert.Equal(ProtectionMode.FullEncrypt, req.Mode);
        Assert.Null(req.IsObfuscate);
    }

    /// <summary>合并动词不能落到错误的工作台（这是 2.1 那类回归的实质）</summary>
    [Fact]
    public void SmartVerbs_MapToDistinctWorkspaces()
    {
        var obf = ShellActionParser.Parse(new[] { "--obfuscate", "x" });
        var sec = ShellActionParser.Parse(new[] { "--secure", "x" });

        Assert.NotEqual(obf.Mode, sec.Mode);
    }

    /// <summary>旧注册表遗留的四个动词仍需可用（升级前装过右键菜单的用户不会失灵）</summary>
    [Theory]
    [InlineData("--hide", ShellActionKind.Obfuscate)]
    [InlineData("--unhide", ShellActionKind.Restore)]
    [InlineData("--encrypt", ShellActionKind.Encrypt)]
    [InlineData("--decrypt", ShellActionKind.Decrypt)]
    public void LegacyVerbs_StillParsed(string verb, ShellActionKind expected)
    {
        var req = ShellActionParser.Parse(new[] { verb, "x" });
        Assert.Equal(expected, req.Kind);
    }
}