using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 结构锁的失败安全：写入失败必须回滚到原始字节，还原失败不得留下"部分解密"的文件。
/// </summary>
public class StructureLockFailureTests : IDisposable
{
    private readonly string _testDir;

    public StructureLockFailureTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Safe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

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

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public void Protect_FooterFailure_RollsBackToOriginalBytes()
    {
        string file = Path.Combine(_testDir, "rollback.png");
        byte[] original = FakePng();
        File.WriteAllBytes(file, original);
        string hash = Sha(original);

        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("pin-123456");
        byte[] mk = cfg.UnwrapMasterKeyWithPin("pin-123456");
        byte[] salt = Convert.FromBase64String(cfg.ConfigSalt!);

        var meta = new V2Metadata
        {
            OriginalFileName = "rollback.png",
            OriginalExtension = ".png",
            LockRegions = StructureLocator.Locate(file, ".png")
        };
        Assert.NotEmpty(meta.LockRegions);

        // pin 为 null 使 footer 构建失败——此时锁定区密文已经写回文件，必须靠回滚救回
        Assert.ThrowsAny<Exception>(() => V2Tail.ProtectInPlace(file, meta, null!, mk, salt));

        Assert.Equal(original.Length, new FileInfo(file).Length);
        Assert.Equal(hash, Sha(File.ReadAllBytes(file)));
        Assert.False(V2Tail.IsTailFile(file));
    }

    [Fact]
    public void Restore_RejectsBodyLengthMismatch_AndLeavesFileUntouched()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("pin-123456");
        cfg.ProtectionMode = ProtectionMode.StructureLock;
        cfg.ObfuscateFileName = false;

        string file = Path.Combine(_testDir, "padded.png");
        File.WriteAllBytes(file, FakePng());
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        byte[] all = File.ReadAllBytes(file);
        int insertAt = (int)read.Metadata.OriginalLength;   // 在 body 末尾插入 1 字节
        byte[] padded = new byte[all.Length + 1];
        Buffer.BlockCopy(all, 0, padded, 0, insertAt);
        Buffer.BlockCopy(all, insertAt, padded, insertAt + 1, all.Length - insertAt);
        File.WriteAllBytes(file, padded);
        string before = Sha(padded);

        var rest = new FileObfuscator().RestoreFile(file, "pin-123456");

        Assert.False(rest.Success, "body 长度与元数据不一致时必须拒绝还原");
        Assert.Equal(before, Sha(File.ReadAllBytes(file)));
    }

    [Fact]
    public void Restore_IntegrityFailure_IsRetrySafe()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ChangePin("pin-123456");
        cfg.ProtectionMode = ProtectionMode.StructureLock;
        cfg.ObfuscateFileName = false;

        string file = Path.Combine(_testDir, "tampered.png");
        File.WriteAllBytes(file, FakePng());
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        var region = read.Metadata.LockRegions.Last();
        byte[] all = File.ReadAllBytes(file);
        all[(int)region.Offset + 1] ^= 0xFF;   // 破坏锁定区密文 → CRC 必然失败
        File.WriteAllBytes(file, all);
        string tampered = Sha(all);

        var first = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.False(first.Success);
        Assert.Equal(tampered, Sha(File.ReadAllBytes(file)));    // 两阶段：失败时不写回任何区

        var second = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.False(second.Success);
        Assert.Equal(tampered, Sha(File.ReadAllBytes(file)));    // 重试不会二次加密导致进一步损坏
    }
}
