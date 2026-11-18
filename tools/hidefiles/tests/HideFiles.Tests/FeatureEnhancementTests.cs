using System;
using System.IO;
using System.Security.Cryptography;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

public sealed class FeatureEnhancementTests : IDisposable
{
    private readonly string _testDir;

    public FeatureEnhancementTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_FeatureTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        HideFilesConfig.DataDirOverride = null;
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    private static byte[] MakeSampleJpeg()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 }); // SOI
        ms.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x06, 0x00, 0x01, 0x02, 0x03 }); // DQT
        ms.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x03, 0x00 }); // SOS
        ms.Write(RandomNumberGenerator.GetBytes(512));
        ms.Write(new byte[] { 0xFF, 0xD9 }); // EOI
        return ms.ToArray();
    }

    [Fact]
    public void PortableMode_DataDirOverride_Works()
    {
        string customDir = Path.Combine(_testDir, "custom_data");
        HideFilesConfig.DataDirOverride = customDir;

        Assert.Equal(customDir, HideFilesConfig.DirPath);
        Assert.Equal(Path.Combine(customDir, "config.json"), HideFilesConfig.GetDefaultConfigPath());
    }

    [Fact]
    public void ExtensionRules_IncludesDocumentsAndOthers()
    {
        var config = HideFilesConfig.CreateDefault();

        Assert.Contains(config.Groups, g => g.Name.Equals("Documents", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(config.Groups, g => g.Name.Equals("Others", StringComparison.OrdinalIgnoreCase));

        // 文档匹配
        Assert.True(config.MatchExtension(".pdf", out string fakePdf));
        Assert.Equal(".dat", fakePdf);

        Assert.True(config.MatchExtension(".txt", out string fakeTxt));
        Assert.Equal(".dat", fakeTxt);

        // Others 匹配
        Assert.True(config.MatchExtension(".exe", out string fakeExe));
        Assert.Equal(".dat", fakeExe);

        // 未知扩展名不会返回 true，但取 Others 的 fakeExt
        Assert.False(config.MatchExtension(".unregistered_xyz", out string fakeOther));
        Assert.Equal(".dat", fakeOther);
    }

    [Fact]
    public void EnsureDefaultGroups_MigratesExistingConfig()
    {
        // 模拟旧配置：只有 4 个组
        var config = new HideFilesConfig
        {
            Groups = new System.Collections.Generic.List<ExtensionRuleGroup>
            {
                new ExtensionRuleGroup("Videos", ".dat", new[] { ".mp4" }),
                new ExtensionRuleGroup("Images", ".tmp", new[] { ".png" })
            }
        };

        HideFilesConfig.EnsureDefaultGroups(config);

        Assert.Contains(config.Groups, g => g.Name.Equals("Documents", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(config.Groups, g => g.Name.Equals("Others", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestoreFile_WithExpectedMode_RejectsMismatch()
    {
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin("pin123456");
        var obfuscator = new FileObfuscator();

        // 1. 创建结构加密文件（Sample JPEG）
        config.ProtectionMode = ProtectionMode.StructureLock;
        string structFile = Path.Combine(_testDir, "sample.jpg");
        File.WriteAllBytes(structFile, MakeSampleJpeg());
        var structObf = obfuscator.ObfuscateFile(structFile, config);
        Assert.True(structObf.Success, structObf.ErrorMessage);

        // 用 StructureLock 期望模式解密应成功
        var goodStruct = obfuscator.RestoreFile(structObf.FinalPath, "pin123456", expectedMode: ProtectionMode.StructureLock);
        Assert.True(goodStruct.Success, goodStruct.ErrorMessage);

        // 重新加密该文件
        var reStructObf = obfuscator.ObfuscateFile(goodStruct.FinalPath, config);
        Assert.True(reStructObf.Success);

        // 用 FullEncrypt 期望模式解密结构加密文件应报错拒绝
        var wrongMode1 = obfuscator.RestoreFile(reStructObf.FinalPath, "pin123456", expectedMode: ProtectionMode.FullEncrypt);
        Assert.False(wrongMode1.Success);
        Assert.Contains("不是整体加密", wrongMode1.ErrorMessage);

        // 2. 创建整体加密文件
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        string fullFile = Path.Combine(_testDir, "document.txt");
        File.WriteAllBytes(fullFile, System.Text.Encoding.UTF8.GetBytes("Important document content"));
        var fullObf = obfuscator.ObfuscateFile(fullFile, config);
        Assert.True(fullObf.Success, fullObf.ErrorMessage);

        // 用 StructureLock 期望模式解密整体加密文件应报错拒绝
        var wrongMode2 = obfuscator.RestoreFile(fullObf.FinalPath, "pin123456", expectedMode: ProtectionMode.StructureLock);
        Assert.False(wrongMode2.Success);
        Assert.Contains("不是结构加密", wrongMode2.ErrorMessage);

        // 用 FullEncrypt 期望模式解密应成功
        var goodFull = obfuscator.RestoreFile(fullObf.FinalPath, "pin123456", expectedMode: ProtectionMode.FullEncrypt);
        Assert.True(goodFull.Success, goodFull.ErrorMessage);
    }

    [Fact]
    public void ObfuscateFile_StructureLock_UnsupportedFormat_FailsWithoutFallback()
    {
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin("pin123456");
        config.ProtectionMode = ProtectionMode.StructureLock;

        string txtFile = Path.Combine(_testDir, "test.txt");
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes("Plain text that cannot be structure locked");
        File.WriteAllBytes(txtFile, originalBytes);

        var obfuscator = new FileObfuscator();
        var res = obfuscator.ObfuscateFile(txtFile, config);

        // 应当直接失败，绝不自动 fallback 到整体加密
        Assert.False(res.Success);
        Assert.Contains("结构加密模式不支持", res.ErrorMessage);
        Assert.True(File.Exists(txtFile));
        Assert.Equal(originalBytes, File.ReadAllBytes(txtFile));
    }
}
