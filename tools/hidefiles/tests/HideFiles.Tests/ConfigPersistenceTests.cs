using System;
using System.IO;
using System.Linq;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 配置持久化健壮性（2.3）。
/// 直接 File.WriteAllText 时，断电/磁盘满会留下半截 JSON；下次 Load 解析失败即静默
/// 丢弃全部密钥材料。用户随后重设密码会生成新主密钥，历史文件用新密码永久打不开。
/// </summary>
public class ConfigPersistenceTests : IDisposable
{
    private readonly string _dir;

    public ConfigPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "HideFiles_Cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void Load_ValidConfig_DoesNotFlagCorruption()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("TestPin12345");
        cfg.Save(ConfigPath);

        var loaded = HideFilesConfig.Load(ConfigPath);

        Assert.False(HideFilesConfig.LastLoadWasCorrupted);
        Assert.Null(HideFilesConfig.LastLoadBackupPath);
        Assert.True(loaded.HasKeyMaterial);
    }

    /// <summary>核心：损坏的配置必须先备份，再回退默认值</summary>
    [Fact]
    public void Load_TruncatedConfig_BacksUpBeforeFallingBackToDefaults()
    {
        var cfg = HideFilesConfig.CreateDefault();
        var change = cfg.ChangePin("TestPin12345");
        cfg.Save(ConfigPath);
        Assert.NotNull(change.RecoveryCode);

        // 模拟断电：JSON 只写了一半
        File.WriteAllText(ConfigPath, "{ \"Version\": 1, \"HeadObf");

        var loaded = HideFilesConfig.Load(ConfigPath);

        Assert.True(HideFilesConfig.LastLoadWasCorrupted);
        Assert.False(loaded.HasKeyMaterial);           // 密钥材料确实丢了

        // 但原始残片被保留下来了，用户仍有手工恢复的机会
        string? backup = HideFilesConfig.LastLoadBackupPath;
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Assert.Contains("HeadObf", File.ReadAllText(backup!));
    }

    /// <summary>备份不能覆盖已有备份（多次损坏时每次都要留档）</summary>
    [Fact]
    public void Load_MultipleCorruptions_KeepSeparateBackups()
    {
        File.WriteAllText(ConfigPath, "corrupt-one");
        HideFilesConfig.Load(ConfigPath);
        string? first = HideFilesConfig.LastLoadBackupPath;

        File.WriteAllText(ConfigPath, "corrupt-two");
        HideFilesConfig.Load(ConfigPath);
        string? second = HideFilesConfig.LastLoadBackupPath;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Contains("corrupt-one", File.ReadAllText(first!));
        Assert.Contains("corrupt-two", File.ReadAllText(second!));
    }

    /// <summary>Save 必须是先写临时文件再替换：成功后目录里不能残留 .tmp</summary>
    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("TestPin12345");
        cfg.Save(ConfigPath);
        cfg.Save(ConfigPath);

        var temps = Directory.GetFiles(_dir, "*.tmp");
        Assert.Empty(temps);
        Assert.True(File.Exists(ConfigPath));
    }

    /// <summary>Save 后原文件必须能被完整解析（不是半截 JSON）</summary>
    [Fact]
    public void Save_ProducesParsableFile_AfterRepeatedWrites()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("TestPin12345");

        for (int i = 0; i < 5; i++)
        {
            cfg.HeadObfuscateSize = CoreConstants.MinHeadSize + i * 1024;
            cfg.Save(ConfigPath);

            var loaded = HideFilesConfig.Load(ConfigPath);
            Assert.False(HideFilesConfig.LastLoadWasCorrupted, $"第 {i} 次写入后配置不可解析");
            Assert.Equal(CoreConstants.MinHeadSize + i * 1024, loaded.HeadObfuscateSize);
        }
    }

    /// <summary>加密文件在配置损坏前后仍可用恢复码还原（证明损坏只影响本机 PIN 路径）</summary>
    [Fact]
    public void CorruptedConfig_RecoveryCodeStillRestoresFile()
    {
        var cfg = HideFilesConfig.CreateDefault();
        var change = cfg.ChangePin("TestPin12345");
        cfg.Save(ConfigPath);
        string recovery = change.RecoveryCode!;

        cfg.SessionPin = "TestPin12345";
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        var obf = new FileObfuscator();
        string src = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(src, "content");
        var enc = obf.ObfuscateFile(src, cfg);
        Assert.True(enc.Success, enc.ErrorMessage);

        // 配置被破坏
        File.WriteAllText(ConfigPath, "{ broken");
        var fallback = HideFilesConfig.Load(ConfigPath);
        Assert.True(HideFilesConfig.LastLoadWasCorrupted);
        Assert.False(fallback.HasKeyMaterial);

        // 恢复码仍可解密（容器自包含）
        var dec = obf.RestoreFile(enc.FinalPath, null, recovery);
        Assert.True(dec.Success, dec.ErrorMessage);
        Assert.Equal("content", File.ReadAllText(dec.FinalPath));
    }

    [Fact]
    public void Config_PropertyChanged_FiresChangedEvent()
    {
        var cfg = HideFilesConfig.CreateDefault();
        int changedCount = 0;
        cfg.Changed += (_, _) => changedCount++;

        cfg.HeadObfuscateSize = 1048576;
        Assert.Equal(1, changedCount);

        cfg.ObfuscateFileName = false;
        Assert.Equal(2, changedCount);

        cfg.IncludeSubdirectories = true;
        Assert.Equal(3, changedCount);

        cfg.Deterministic = true;
        Assert.Equal(4, changedCount);

        // 重复设置相同值不应触发
        cfg.Deterministic = true;
        Assert.Equal(4, changedCount);
    }

    [Fact]
    public void ExtensionRuleGroup_PropertyChanged_BubblesToConfigChanged()
    {
        var cfg = HideFilesConfig.CreateDefault();
        int changedCount = 0;
        cfg.Changed += (_, _) => changedCount++;

        var group = cfg.Groups.First();
        group.Enabled = !group.Enabled;
        Assert.Equal(1, changedCount);

        group.FakeExtension = ".custom";
        Assert.Equal(2, changedCount);

        // 重复设置相同值不应触发
        group.FakeExtension = ".custom";
        Assert.Equal(2, changedCount);
    }
}
