using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 损坏输入与收集规则的健壮性：畸形分块长度、畸形元数据、任意扩展名的整体加密。
/// </summary>
public class RobustnessTests : IDisposable
{
    private readonly string _testDir;

    public RobustnessTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Robust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    private static HideFilesConfig MakeConfig()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        cfg.ChangePin("pin-123456");
        return cfg;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(CoreConstants.FullChunkSize + 1)]
    public void FullEncrypt_CorruptedChunkLength_ReportsInvalidContainer(int corruptedLength)
    {
        var cfg = MakeConfig();
        string file = Path.Combine(_testDir, $"chunk_{corruptedLength}.mp4");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(5000));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        byte[] all = File.ReadAllBytes(obf.FinalPath);
        var front = V2Container.Read(obf.FinalPath, "pin-123456", null);
        BitConverter.GetBytes(corruptedLength).CopyTo(all, (int)front.BodyOffset);
        File.WriteAllBytes(obf.FinalPath, all);

        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, "pin-123456");

        Assert.False(rest.Success);
        Assert.Contains("容器已损坏", rest.ErrorMessage);
    }

    [Fact]
    public void Metadata_Deserialize_SurvivesAbsurdLengths()
    {
        // tag=4（区域清单）+ 超大 varint 长度：必须安全终止而不是抛异常
        byte[] absurd = { 4, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00 };
        var meta = V2Metadata.Deserialize(absurd);
        Assert.Empty(meta.LockRegions);

        // 截断的 TLV（声明 8 字节但只有 2 字节）
        byte[] truncated = { 3, 8, 0x01, 0x02 };
        var meta2 = V2Metadata.Deserialize(truncated);
        Assert.Equal(0, meta2.OriginalLength);
    }

    [Fact]
    public void CollectFiles_IncludesAnyExtension_WhenRulesDisabled()
    {
        var cfg = MakeConfig();
        string file = Path.Combine(_testDir, "data.xyz");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(256));

        var processor = new BatchProcessor();
        var byRules = processor.CollectFiles(new[] { _testDir }, cfg, forObfuscate: true);
        var anyExtension = processor.CollectFiles(new[] { _testDir }, cfg, forObfuscate: true,
            filterByExtensionRules: false);

        Assert.Empty(byRules);                       // .xyz 不在任何规则组内
        Assert.Single(anyExtension);                 // 整体加密应当收集到它
    }

    [Fact]
    public void CollectFiles_SkipsAlreadyProtectedFiles()
    {
        var cfg = MakeConfig();
        string file = Path.Combine(_testDir, "data.xyz");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(256));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        var processor = new BatchProcessor();
        var collected = processor.CollectFiles(new[] { _testDir }, cfg, forObfuscate: true,
            filterByExtensionRules: false);

        Assert.Empty(collected);                     // 已是容器，不应再次进入待加密清单
    }

    [Fact]
    public void Restore_Failure_LeavesNoPlaintextTempFile()
    {
        var cfg = MakeConfig();
        string file = Path.Combine(_testDir, "leak.mp4");
        byte[] content = RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(file, content);

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        // 破坏最后一个分块的认证标签，使还原中途失败
        byte[] all = File.ReadAllBytes(obf.FinalPath);
        all[^1] ^= 0xFF;
        File.WriteAllBytes(obf.FinalPath, all);

        int before = Directory.GetFiles(_testDir).Length;
        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, "pin-123456");

        Assert.False(rest.Success);
        Assert.Equal(before, Directory.GetFiles(_testDir).Length);   // 不留下 hf_rst_*.tmp 明文
        Assert.DoesNotContain(Directory.GetFiles(_testDir), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }
}
