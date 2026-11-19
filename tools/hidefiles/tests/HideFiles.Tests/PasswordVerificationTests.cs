using System;
using System.IO;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 密码校验链路（2.4 最小版）。
/// 原先只比对 PinHash（单轮 SHA-256），一旦 config 中的哈希与密钥材料不一致，
/// 用户会"验证通过但解密永远失败"。改为以"能否解开 WrappedMasterKey"为准。
/// </summary>
public class PasswordVerificationTests : IDisposable
{
    private readonly string _dir;

    public PasswordVerificationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "HideFiles_Pwd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { }
    }

    /// <summary>UI 层 VerifyPassword 的行为等价物：有密钥材料时以 unwrap 结果为准</summary>
    private static bool VerifyPasswordLikeUi(HideFilesConfig config, string input)
    {
        if (config.HasKeyMaterial)
        {
            try { config.UnwrapMasterKeyWithPin(input); return true; }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                          or FormatException or InvalidOperationException)
            { return false; }
        }
        return config.VerifyPin(input);
    }

    [Fact]
    public void CorrectPassword_Verifies()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("CorrectPin12345");
        Assert.True(VerifyPasswordLikeUi(cfg, "CorrectPin12345"));
    }

    [Fact]
    public void WrongPassword_Rejected()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("CorrectPin12345");
        Assert.False(VerifyPasswordLikeUi(cfg, "WrongPin12345"));
        Assert.False(VerifyPasswordLikeUi(cfg, ""));
    }

    /// <summary>
    /// 核心场景：密钥材料被篡改。
    /// 去掉 PinHash 后，密码校验完全由真实密钥材料 unwrap 决定，必须拒绝。
    /// </summary>
    [Fact]
    public void InconsistentKeyMaterial_DoesNotFalselyAccept()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("CorrectPin12345");

        // 换成用另一个密码封装的密钥材料：
        var other = HideFilesConfig.CreateDefault();
        other.ChangePin("SomeOtherPin999");
        cfg.WrappedMasterKey = other.WrappedMasterKey;
        cfg.ConfigSalt = other.ConfigSalt;

        // VerifyPin 与 UI 校验都以真实密钥材料为准，直接拒绝
        Assert.False(cfg.VerifyPin("CorrectPin12345"));
        Assert.False(VerifyPasswordLikeUi(cfg, "CorrectPin12345"));
    }

    /// <summary>没有密钥材料的配置视为未设置密码</summary>
    [Fact]
    public void ConfigWithoutKeyMaterial_ReportsNoPin()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("LegacyPin12345");
        cfg.ConfigSalt = null;
        cfg.WrappedMasterKey = null;

        Assert.False(cfg.HasKeyMaterial);
        Assert.False(cfg.HasPin);
    }

    /// <summary>改密码后新密码可解、旧密码不可解（且都以 unwrap 为准）</summary>
    [Fact]
    public void AfterPinChange_NewPasswordVerifies_OldDoesNot()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("OldPin123456");
        var change = cfg.ChangePin("NewPin123456", currentPin: "OldPin123456");
        Assert.True(change.Success);
        Assert.False(change.MasterKeyRecreated);   // 只重封装，不换主密钥

        Assert.True(VerifyPasswordLikeUi(cfg, "NewPin123456"));
        Assert.False(VerifyPasswordLikeUi(cfg, "OldPin123456"));
    }
}
