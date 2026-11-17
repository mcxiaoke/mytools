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
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void MetadataBlock_SerializeAndDeserialize_RoundTripMatches()
    {
        var originalHead = Encoding.UTF8.GetBytes("SAMPLE_HEAD_DATA_1234567890_XYZ");
        var meta = new MetadataBlock
        {
            OriginalFileName = "my_sample_video.mp4",
            OriginalExtension = ".mp4",
            OriginalLength = 1048576,
            ObfuscatedHeadLength = originalHead.Length,
            HeadChecksum = Crc32Helper.Compute(originalHead),
            OriginalHeadBytes = originalHead
        };

        byte[] serialized = meta.Serialize();
        Assert.NotEmpty(serialized);

        using var ms = new MemoryStream(serialized);
        var restored = MetadataBlock.ReadFromStream(ms);

        Assert.NotNull(restored);
        Assert.Equal(meta.OriginalFileName, restored.OriginalFileName);
        Assert.Equal(meta.OriginalExtension, restored.OriginalExtension);
        Assert.Equal(meta.OriginalLength, restored.OriginalLength);
        Assert.Equal(meta.ObfuscatedHeadLength, restored.ObfuscatedHeadLength);
        Assert.Equal(meta.HeadChecksum, restored.HeadChecksum);
        Assert.Equal(meta.OriginalHeadBytes, restored.OriginalHeadBytes);
    }

    [Theory]
    [InlineData(16)]          // 小于 64KB
    [InlineData(1024)]        // 1 KB
    [InlineData(64 * 1024)]   // 64 KB
    [InlineData(256 * 1024)]  // 256 KB
    public void FileObfuscator_ObfuscateAndRestore_BitForBitIdentical(int fileSize)
    {
        var obfuscator = new FileObfuscator();
        var config = HideFilesConfig.CreateDefault();

        string filePath = Path.Combine(_testDir, $"test_{fileSize}.mp4");
        byte[] originalContent = new byte[fileSize];
        Random.Shared.NextBytes(originalContent);
        File.WriteAllBytes(filePath, originalContent);

        string originalHash = Convert.ToHexString(SHA256.HashData(originalContent));

        // 1. 探测状态：应为 Normal
        var preInspect = obfuscator.Inspect(filePath);
        Assert.Equal(FileStatus.Normal, preInspect.Status);

        // 2. 执行混淆
        var obfResult = obfuscator.ObfuscateFile(filePath, config);
        Assert.True(obfResult.Success, obfResult.ErrorMessage);
        Assert.True(File.Exists(obfResult.FinalPath));
        Assert.EndsWith(".dat", obfResult.FinalPath);

        // 3. 检查混淆后状态：应为 Obfuscated
        var midInspect = obfuscator.Inspect(obfResult.FinalPath);
        Assert.Equal(FileStatus.Obfuscated, midInspect.Status);
        Assert.Equal("test_" + fileSize + ".mp4", midInspect.OriginalFileName);

        // 4. 重复混淆防呆：对已混淆文件再次执行混淆必须失败
        var reObf = obfuscator.ObfuscateFile(obfResult.FinalPath, config);
        Assert.False(reObf.Success);
        Assert.Contains("Already obfuscated", reObf.ErrorMessage);

        // 5. 执行还原
        var restResult = obfuscator.RestoreFile(obfResult.FinalPath);
        Assert.True(restResult.Success, restResult.ErrorMessage);
        Assert.True(File.Exists(restResult.FinalPath));
        Assert.EndsWith(".mp4", restResult.FinalPath);

        // 6. 比对哈希：必须 100% Bit-for-bit 一致
        byte[] restoredContent = File.ReadAllBytes(restResult.FinalPath);
        string restoredHash = Convert.ToHexString(SHA256.HashData(restoredContent));
        Assert.Equal(originalHash, restoredHash);
    }

    [Fact]
    public void DirectoryObfuscator_RoundTrip_MatchesOriginal()
    {
        string[] sampleNames = new[]
        {
            "Personal_Photos",
            "秘密资料_2026",
            "DeepFolder.Test-01",
            "Special!@#%^&()_+",
            "Simple"
        };

        foreach (var name in sampleNames)
        {
            string obfuscated = DirectoryObfuscator.ObfuscateName(name);
            Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, obfuscated);

            bool success = DirectoryObfuscator.TryRestoreName(obfuscated, out string restored);
            Assert.True(success);
            Assert.Equal(name, restored);
        }
    }

    [Fact]
    public void Config_PinProtection_VerificationWorks()
    {
        var config = HideFilesConfig.CreateDefault();
        Assert.False(config.HasPin);
        Assert.True(config.VerifyPin(null)); // 未设置时无门控

        // 设置 PIN
        config.SetPin("123456");
        Assert.True(config.HasPin);
        Assert.True(config.VerifyPin("123456"));
        Assert.False(config.VerifyPin("wrong_pin"));
        Assert.False(config.VerifyPin(""));

        // 清除 PIN
        config.SetPin(null);
        Assert.False(config.HasPin);
        Assert.True(config.VerifyPin(null));
    }

    [Fact]
    public void Config_SaveAndLoad_PreservesPinAcrossRestart()
    {
        // 回归测试：Save() 通过 JsonStringEnumConverter 把枚举写成字符串（如 "HashStem"），
        // Load() 必须使用同样的 Converter，否则反序列化会抛异常并被 catch 吞掉，
        // 静默返回默认配置，导致已保存的 PIN 丢失、界面显示"未设置 PIN"。
        var config = HideFilesConfig.CreateDefault();
        config.SetPin("123456");
        string path = Path.Combine(_testDir, "config.json");
        config.Save(path);

        string raw = File.ReadAllText(path);
        Assert.Contains("\"HashStem\"", raw); // 确认枚举确实以字符串写出

        var loaded = HideFilesConfig.Load(path);
        Assert.True(loaded.HasPin, "PinHash 应在 Save/Load 往返后保留");
        Assert.True(loaded.VerifyPin("123456"));
        Assert.False(loaded.VerifyPin("wrong_pin"));
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
