using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

public class CoreTests : IDisposable
{
    private readonly string _testDir;

    public CoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    private static HideFilesConfig MakeConfig(string pin)
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.SetPinV2(pin);
        // 测试样本为随机字节，无有效媒体结构，显式用整体加密模式
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        return cfg;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    [InlineData(256 * 1024)]
    public void FileObfuscator_V2_RoundTrip_BitForBitIdentical(int fileSize)
    {
        var obfuscator = new FileObfuscator();
        var config = MakeConfig("123456");

        string filePath = Path.Combine(_testDir, $"test_{fileSize}.mp4");
        byte[] originalContent = new byte[fileSize];
        Random.Shared.NextBytes(originalContent);
        File.WriteAllBytes(filePath, originalContent);
        string originalHash = Convert.ToHexString(SHA256.HashData(originalContent));

        Assert.Equal(FileStatus.Normal, obfuscator.Inspect(filePath).Status);

        var obf = obfuscator.ObfuscateFile(filePath, config);
        Assert.True(obf.Success, obf.ErrorMessage);
        Assert.True(File.Exists(obf.FinalPath));
        Assert.True(V2Container.IsV2File(obf.FinalPath), "输出应为 v2 容器");
        Assert.Equal(FileStatus.Obfuscated, obfuscator.Inspect(obf.FinalPath).Status);

        // 重复混淆防呆
        var reObf = obfuscator.ObfuscateFile(obf.FinalPath, config);
        Assert.False(reObf.Success);

        var rest = obfuscator.RestoreFile(obf.FinalPath, "123456");
        Assert.True(rest.Success, rest.ErrorMessage);
        string restoredHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rest.FinalPath)));
        Assert.Equal(originalHash, restoredHash);
    }

    [Fact]
    public void V2_StructureLock_NoStructure_ReportsError()
    {
        var config = HideFilesConfig.CreateDefault();
        config.SetPinV2("123456");
        config.ProtectionMode = ProtectionMode.StructureLock; // 默认模式
        string filePath = Path.Combine(_testDir, "random.mp4"); // 随机内容，非真实 MP4
        File.WriteAllBytes(filePath, RandomNumberGenerator.GetBytes(4096));

        var result = new FileObfuscator().ObfuscateFile(filePath, config);
        Assert.False(result.Success);
        Assert.Contains("整体加密", result.ErrorMessage);
        Assert.True(File.Exists(filePath), "失败时原文件必须保持不变");
        Assert.False(V2Container.IsV2File(filePath));
    }

    [Fact]
    public void V2_WrongPin_Rejected()
    {
        var obfuscator = new FileObfuscator();
        var config = MakeConfig("right-pin");
        string filePath = Path.Combine(_testDir, "secret.bin");
        File.WriteAllBytes(filePath, RandomNumberGenerator.GetBytes(4096));

        var obf = obfuscator.ObfuscateFile(filePath, config);
        Assert.True(obf.Success, obf.ErrorMessage);

        var wrong = obfuscator.RestoreFile(obf.FinalPath, "wrong-pin");
        Assert.False(wrong.Success);
        Assert.True(File.Exists(obf.FinalPath), "错误 PIN 不应删除混淆文件");
    }

    [Fact]
    public void V2_RecoveryCode_RestoresWithoutPin()
    {
        var obfuscator = new FileObfuscator();
        var config = MakeConfig("123456");
        string recovery = config.SetPinV2("123456");
        Assert.True(config.VerifyRecoveryCode(recovery));

        string filePath = Path.Combine(_testDir, "recover.dat");
        byte[] content = RandomNumberGenerator.GetBytes(8192);
        File.WriteAllBytes(filePath, content);
        string hash = Convert.ToHexString(SHA256.HashData(content));

        var obf = obfuscator.ObfuscateFile(filePath, config);
        Assert.True(obf.Success, obf.ErrorMessage);

        // 仅用恢复码还原（不传 PIN）
        var rest = obfuscator.RestoreFile(obf.FinalPath, null, recovery);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rest.FinalPath))));
    }

    [Fact]
    public void V2_Tampering_DetectedBySha256()
    {
        var obfuscator = new FileObfuscator();
        var config = MakeConfig("123456");
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        string filePath = Path.Combine(_testDir, "tamper.mp4");
        File.WriteAllBytes(filePath, RandomNumberGenerator.GetBytes(200000));

        var obf = obfuscator.ObfuscateFile(filePath, config);
        Assert.True(obf.Success, obf.ErrorMessage);

        // 篡改密文主体一个字节（跳过 120 字节头，改末尾）
        byte[] all = File.ReadAllBytes(obf.FinalPath);
        all[^1] ^= 0xFF;
        File.WriteAllBytes(obf.FinalPath, all);

        var rest = obfuscator.RestoreFile(obf.FinalPath, "123456");
        Assert.False(rest.Success, "篡改后应校验失败");
    }

    [Fact]
    public void V2Metadata_TlvRoundTrip()
    {
        var meta = new V2Metadata
        {
            OriginalFileName = "video.mp4", OriginalExtension = ".mp4",
            OriginalLength = 1234567, KdfIterations = 600000,
            Mode = ProtectionMode.StructureLock,
            OriginalSha256 = RandomNumberGenerator.GetBytes(32),
            LockRegions = new() { new LockRegion(100, 256, "stco"), new LockRegion(4096, 64, "avcC") }
        };
        byte[] bytes = meta.Serialize();
        var back = V2Metadata.Deserialize(bytes);
        Assert.Equal(meta.OriginalFileName, back.OriginalFileName);
        Assert.Equal(meta.OriginalLength, back.OriginalLength);
        Assert.Equal(meta.Mode, back.Mode);
        Assert.Equal(2, back.LockRegions.Count);
        Assert.Equal(100, back.LockRegions[0].Offset);
        Assert.Equal("stco", back.LockRegions[0].Kind);
    }

    [Fact]
    public void Base32_RoundTrip()
    {
        byte[] key = RandomNumberGenerator.GetBytes(CoreConstants.MasterKeySize);
        string code = V2Crypto.MasterKeyToRecoveryCode(key);
        Assert.Equal(key, V2Crypto.RecoveryCodeToMasterKey(code));
    }

    [Fact]
    public void DirectoryObfuscator_RoundTrip_MatchesOriginal()
    {
        string[] sampleNames = { "Personal_Photos", "秘密资料_2026", "DeepFolder.Test-01", "Special!@#%^&()_+", "Simple" };
        foreach (var name in sampleNames)
        {
            string obfuscated = DirectoryObfuscator.ObfuscateName(name);
            Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, obfuscated);
            Assert.True(DirectoryObfuscator.TryRestoreName(obfuscated, out string restored));
            Assert.Equal(name, restored);
        }
    }

    [Fact]
    public void Config_PinProtection_VerificationWorks()
    {
        var config = HideFilesConfig.CreateDefault();
        Assert.False(config.HasPin);
        Assert.True(config.VerifyPin(null));

        config.SetPin("123456");
        Assert.True(config.HasPin);
        Assert.True(config.VerifyPin("123456"));
        Assert.False(config.VerifyPin("wrong_pin"));
        Assert.False(config.VerifyPin(""));

        config.SetPin(null);
        Assert.False(config.HasPin);
    }

    [Fact]
    public void Config_SaveAndLoad_PreservesPinAcrossRestart()
    {
        var config = HideFilesConfig.CreateDefault();
        config.SetPin("123456");
        string path = Path.Combine(_testDir, "config.json");
        config.Save(path);

        string raw = File.ReadAllText(path);
        Assert.Contains("\"HashStem\"", raw);

        var loaded = HideFilesConfig.Load(path);
        Assert.True(loaded.HasPin);
        Assert.True(loaded.VerifyPin("123456"));
        Assert.False(loaded.VerifyPin("wrong_pin"));
        Assert.NotNull(loaded.WrappedMasterKey);
        Assert.NotNull(loaded.ConfigSalt);
        Assert.Equal(NameMode.HashStem, loaded.NameObfuscationMode);
    }

    [Fact]
    public void Config_RuleMatching_WorksCorrectly()
    {
        var config = HideFilesConfig.CreateDefault();
        Assert.True(config.MatchExtension(".mp4", out string fakeExt1));
        Assert.Equal(".dat", fakeExt1);
        Assert.True(config.MatchExtension(".PNG", out string fakeExt2));
        Assert.Equal(".tmp", fakeExt2);
        Assert.True(config.MatchExtension(".flac", out string fakeExt3));
        Assert.Equal(".bin", fakeExt3);
        Assert.False(config.MatchExtension(".unknown_ext_xyz", out string fakeExt4));
        Assert.Equal(config.DefaultFakeExtension, fakeExt4);
    }
}
