using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 目标路径语义：关闭文件名混淆必须保留原名；还原/混淆都不得覆盖磁盘上已存在的同名文件。
/// </summary>
public class TargetPathTests : IDisposable
{
    private readonly string _testDir;

    public TargetPathTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Target_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    /// <summary>含合法 PNG signature + IHDR 的最小文件，使结构锁能定位到关键结构</summary>
    private static byte[] FakePng(int bodyLen = 2048)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        ms.Write(new byte[] { 0, 0, 0, 13 });
        ms.Write(Encoding.ASCII.GetBytes("IHDR"));
        ms.Write(RandomNumberGenerator.GetBytes(13));
        ms.Write(new byte[4]);
        ms.Write(RandomNumberGenerator.GetBytes(bodyLen));
        return ms.ToArray();
    }

    private static HideFilesConfig MakeConfig(ProtectionMode mode, bool obfuscateName)
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("pin-123456");
        cfg.ProtectionMode = mode;
        cfg.ObfuscateFileName = obfuscateName;
        return cfg;
    }

    [Fact]
    public void StructureLock_ObfuscateFileNameDisabled_KeepsOriginalName()
    {
        var cfg = MakeConfig(ProtectionMode.StructureLock, obfuscateName: false);
        string file = Path.Combine(_testDir, "photo.png");
        byte[] original = FakePng();
        File.WriteAllBytes(file, original);
        string hash = Convert.ToHexString(SHA256.HashData(original));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);

        Assert.True(obf.Success, obf.ErrorMessage);
        Assert.Equal("photo.png", Path.GetFileName(obf.FinalPath));
        Assert.True(V2Tail.IsTailFile(file));

        var rest = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal("photo.png", Path.GetFileName(rest.FinalPath));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Fact]
    public void FullEncrypt_ObfuscateFileNameDisabled_ReplacesInPlace()
    {
        var cfg = MakeConfig(ProtectionMode.FullEncrypt, obfuscateName: false);
        string file = Path.Combine(_testDir, "doc.mp4");
        byte[] original = RandomNumberGenerator.GetBytes(9000);
        File.WriteAllBytes(file, original);
        string hash = Convert.ToHexString(SHA256.HashData(original));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);

        Assert.True(obf.Success, obf.ErrorMessage);
        Assert.Equal(file, obf.FinalPath);
        Assert.True(V2Container.IsV2File(file), "关闭文件名混淆时原文件应被容器原地替换");

        var rest = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Fact]
    public void FullEncrypt_Restore_DoesNotOverwriteExistingFileWithSameName()
    {
        var cfg = MakeConfig(ProtectionMode.FullEncrypt, obfuscateName: true);
        string file = Path.Combine(_testDir, "doc.mp4");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("ORIGINAL"));
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        // 用户在还原前又创建了同名文件
        byte[] later = Encoding.UTF8.GetBytes("CREATED-AFTER");
        File.WriteAllBytes(file, later);

        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, "pin-123456");

        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal("doc (1).mp4", Path.GetFileName(rest.FinalPath));
        Assert.Equal("CREATED-AFTER", File.ReadAllText(file));
        Assert.Equal("ORIGINAL", File.ReadAllText(rest.FinalPath));
    }

    [Fact]
    public void StructureLock_Restore_DoesNotOverwriteExistingFileWithSameName()
    {
        var cfg = MakeConfig(ProtectionMode.StructureLock, obfuscateName: true);
        string file = Path.Combine(_testDir, "photo.png");
        byte[] original = FakePng();
        File.WriteAllBytes(file, original);
        string hash = Convert.ToHexString(SHA256.HashData(original));

        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        byte[] later = FakePng(64);
        File.WriteAllBytes(file, later);
        string laterHash = Convert.ToHexString(SHA256.HashData(later));

        var rest = new FileObfuscator().RestoreFile(obf.FinalPath, "pin-123456");

        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal("photo (1).png", Path.GetFileName(rest.FinalPath));
        Assert.Equal(laterHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rest.FinalPath))));
    }

    [Fact]
    public void Obfuscate_DoesNotOverwriteExistingTargetName()
    {
        // 两个同内容同长度的文件会产生同一个哈希名，第二个不得覆盖第一个
        var cfg = MakeConfig(ProtectionMode.FullEncrypt, obfuscateName: true);
        string a = Path.Combine(_testDir, "a.mp4");
        byte[] payload = RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(a, payload);

        var first = new FileObfuscator().ObfuscateFile(a, cfg);
        Assert.True(first.Success, first.ErrorMessage);

        // 同名同长度的副本（哈希名与第一个相同）
        string b = Path.Combine(_testDir, "a.mp4");
        File.WriteAllBytes(b, payload);
        var second = new FileObfuscator().ObfuscateFile(b, cfg);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.NotEqual(first.FinalPath, second.FinalPath);
        Assert.True(File.Exists(first.FinalPath));
        Assert.True(File.Exists(second.FinalPath));

        // 两个容器都能各自还原出一份内容
        var r1 = new FileObfuscator().RestoreFile(first.FinalPath, "pin-123456");
        var r2 = new FileObfuscator().RestoreFile(second.FinalPath, "pin-123456");
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.NotEqual(r1.FinalPath, r2.FinalPath);
    }
}
