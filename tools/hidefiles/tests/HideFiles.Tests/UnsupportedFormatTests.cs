using System;
using System.IO;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// "不支持"状态（用户反馈：结构加密 tab 里不支持的格式不该显示为"失败"）。
/// 这类文件并没有出错，只是需要换到加密工作台，因此必须与真正的处理失败区分开。
/// </summary>
public class UnsupportedFormatTests : IDisposable
{
    private readonly string _dir;

    public UnsupportedFormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "HideFiles_Unsup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { }
    }

    private HideFilesConfig NewConfig(ProtectionMode mode)
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("TestPin12345");
        cfg.ProtectionMode = mode;
        return cfg;
    }

    [Theory]
    [InlineData(".mp4", true)]
    [InlineData(".MP4", true)]
    [InlineData(".mov", true)]
    [InlineData(".m4v", true)]
    [InlineData(".mkv", true)]
    [InlineData(".webm", true)]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".png", true)]
    [InlineData(".txt", false)]
    [InlineData(".pdf", false)]
    [InlineData(".docx", false)]
    [InlineData(".zip", false)]
    [InlineData("", false)]
    public void IsSupportedExtension_MatchesLocatorScope(string ext, bool expected)
    {
        Assert.Equal(expected, StructureLocator.IsSupportedExtension(ext));
    }

    /// <summary>混淆工作台：不支持的格式在加载时就标为 Unsupported，而不是等到处理时失败</summary>
    [Fact]
    public void Inspect_InStructureWorkspace_MarksUnsupportedFormat()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");

        var insp = new FileObfuscator().Inspect(txt, null, forStructureWorkspace: true);

        Assert.Equal(FileStatus.Unsupported, insp.Status);
        Assert.Contains("不支持", insp.ErrorMessage);
        Assert.Contains("加密", insp.ErrorMessage);   // 引导到加密工作台
    }

    /// <summary>同一个 .txt 在加密工作台是正常可加密文件，不能标成不支持</summary>
    [Fact]
    public void Inspect_InEncryptWorkspace_TreatsSameFileAsNormal()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");

        var insp = new FileObfuscator().Inspect(txt, null, forStructureWorkspace: false);

        Assert.Equal(FileStatus.Normal, insp.Status);
        Assert.Null(insp.ErrorMessage);
    }

    /// <summary>ObfuscateFile 对不支持格式返回 UnsupportedFormat，而不是一般失败</summary>
    [Fact]
    public void Obfuscate_UnsupportedFormat_ReturnsUnsupportedKind()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");

        var res = new FileObfuscator().ObfuscateFile(txt, NewConfig(ProtectionMode.StructureLock));

        Assert.False(res.Success);
        Assert.Equal(ProcessErrorKind.UnsupportedFormat, res.ErrorKind);
        Assert.Contains("不支持", res.ErrorMessage);
        Assert.True(File.Exists(txt));   // 原文件不动
    }

    /// <summary>格式支持但字节不是有效媒体 → 那不是"不支持"，而是文件异常</summary>
    [Fact]
    public void Obfuscate_SupportedExtensionButBrokenContent_ReturnsFailedKind()
    {
        string mp4 = Path.Combine(_dir, "broken.mp4");
        File.WriteAllBytes(mp4, new byte[512]);

        var res = new FileObfuscator().ObfuscateFile(mp4, NewConfig(ProtectionMode.StructureLock));

        Assert.False(res.Success);
        Assert.Equal(ProcessErrorKind.Failed, res.ErrorKind);
        Assert.DoesNotContain("不支持", res.ErrorMessage);
        Assert.True(File.Exists(mp4));
    }

    /// <summary>加密模式对任意格式都有效，不能出现"不支持"</summary>
    [Fact]
    public void Encrypt_AnyFormat_Succeeds()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");

        var res = new FileObfuscator().ObfuscateFile(txt, NewConfig(ProtectionMode.FullEncrypt));

        Assert.True(res.Success, res.ErrorMessage);
        Assert.Equal(ProcessErrorKind.None, res.ErrorKind);
    }

    /// <summary>重复处理归为 AlreadyProcessed，不是失败</summary>
    [Fact]
    public void Obfuscate_AlreadyProcessed_ReturnsAlreadyProcessedKind()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");
        var cfg = NewConfig(ProtectionMode.FullEncrypt);

        var first = new FileObfuscator().ObfuscateFile(txt, cfg);
        Assert.True(first.Success, first.ErrorMessage);

        var second = new FileObfuscator().ObfuscateFile(first.FinalPath, cfg);
        Assert.False(second.Success);
        Assert.Equal(ProcessErrorKind.AlreadyProcessed, second.ErrorKind);
    }

    /// <summary>模式不匹配（在混淆工作台还原加密文件）归为 ModeMismatch</summary>
    [Fact]
    public void Restore_WrongWorkspace_ReturnsModeMismatch()
    {
        string txt = Path.Combine(_dir, "note.txt");
        File.WriteAllText(txt, "hello");
        var cfg = NewConfig(ProtectionMode.FullEncrypt);
        var enc = new FileObfuscator().ObfuscateFile(txt, cfg);
        Assert.True(enc.Success, enc.ErrorMessage);

        var res = new FileObfuscator().RestoreFile(
            enc.FinalPath, "TestPin12345", expectedMode: ProtectionMode.StructureLock);

        Assert.False(res.Success);
        Assert.Equal(ProcessErrorKind.ModeMismatch, res.ErrorKind);
        Assert.Contains("不是混淆文件", res.ErrorMessage);
        Assert.Contains("加密", res.ErrorMessage);
    }
}
