using System;
using System.IO;
using System.Security.Cryptography;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// PIN 变更语义：改 PIN 必须重封装同一主密钥（恢复码不变、历史文件仍可解），
/// 而不是重建主密钥让历史文件当场失效。
/// </summary>
public class PinChangeTests : IDisposable
{
    private readonly string _testDir;

    public PinChangeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Pin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    [Fact]
    public void ChangePin_FirstTime_CreatesMasterKeyAndRecoveryCode()
    {
        var cfg = HideFilesConfig.CreateDefault();
        Assert.False(cfg.HasKeyMaterial);

        var result = cfg.ChangePin("first-pin");

        Assert.True(result.Success);
        Assert.True(result.MasterKeyRecreated);
        Assert.False(string.IsNullOrWhiteSpace(result.RecoveryCode));
        Assert.True(cfg.HasPin);
        Assert.True(cfg.HasKeyMaterial);
        Assert.True(cfg.VerifyPin("first-pin"));
        Assert.True(cfg.VerifyRecoveryCode(result.RecoveryCode));
    }

    [Fact]
    public void ChangePin_RequiresCurrentPin_WhenKeyMaterialExists()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("old-pin");

        var result = cfg.ChangePin("new-pin");   // 未提供当前 PIN

        Assert.False(result.Success);
        Assert.True(cfg.VerifyPin("old-pin"), "被拒绝的变更不得改动现有密钥材料");
        Assert.False(cfg.VerifyPin("new-pin"));
    }

    [Fact]
    public void ChangePin_WrongCurrentPin_IsRejected()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("old-pin");

        var result = cfg.ChangePin("new-pin", "definitely-wrong");

        Assert.False(result.Success);
        Assert.True(cfg.VerifyPin("old-pin"));
    }

    [Fact]
    public void ChangePin_KeepsMasterKey_SoOldFilesStayDecryptable()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        string recovery = cfg.ChangePin("old-pin").RecoveryCode!;

        string file = Path.Combine(_testDir, "legacy.mp4");
        byte[] content = RandomNumberGenerator.GetBytes(5000);
        File.WriteAllBytes(file, content);
        string hash = Convert.ToHexString(SHA256.HashData(content));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        var changed = cfg.ChangePin("new-pin", "old-pin");
        Assert.True(changed.Success, changed.ErrorMessage);
        Assert.False(changed.MasterKeyRecreated, "改 PIN 不应重建主密钥");
        Assert.Null(changed.RecoveryCode);
        Assert.True(cfg.VerifyRecoveryCode(recovery), "主密钥未变，恢复码必须保持有效");

        // 新 PIN 能解老文件：文件内 emkBlob 仍是旧 PIN 封装，由本机 config 的主密钥兜底
        byte[] fallback = cfg.UnwrapMasterKeyWithPin("new-pin");
        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, "new-pin", null, fallback);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rest.FinalPath))));
    }

    [Fact]
    public void OldRecoveryCode_StillWorks_AfterPinChange()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        string recovery = cfg.ChangePin("old-pin").RecoveryCode!;

        string file = Path.Combine(_testDir, "recovery.mp4");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(3000));
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        cfg.ChangePin("new-pin", "old-pin");

        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, null, recovery);
        Assert.True(rest.Success, rest.ErrorMessage);
    }

    [Fact]
    public void ClearKeyMaterial_RemovesAllPinState()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("pin-123456");

        cfg.ClearKeyMaterial();

        Assert.False(cfg.HasPin);
        Assert.False(cfg.HasKeyMaterial);
        Assert.Null(cfg.SessionPin);
        Assert.Null(cfg.RecoveryCodeHash);
    }
}
