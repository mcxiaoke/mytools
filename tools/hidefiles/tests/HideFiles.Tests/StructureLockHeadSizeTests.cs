using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 头部混淆区（默认 64KB）：长度可配置、合并只扩不缩、PNG 大 IDAT 仍可定位。
/// </summary>
public class StructureLockHeadSizeTests : IDisposable
{
    private readonly string _testDir;

    public StructureLockHeadSizeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Head_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    /// <summary>PNG signature + IHDR + 伪数据（无 IDAT），总长 = 33 + bodyLen</summary>
    private static byte[] FakePng(int bodyLen)
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

    private static HideFilesConfig MakeConfig(int? headSize = null)
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.SetPinV2("pin-123456");
        cfg.ProtectionMode = ProtectionMode.StructureLock;
        cfg.ObfuscateFileName = false;
        if (headSize.HasValue) cfg.HeadObfuscateSize = headSize.Value;
        return cfg;
    }

    [Fact]
    public void HeadRegion_DefaultsTo64KB()
    {
        string file = Path.Combine(_testDir, "big.png");
        File.WriteAllBytes(file, FakePng(200_000));

        var obf = new FileObfuscator().ObfuscateFile(file, MakeConfig());
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        var head = read.Metadata.LockRegions[0];
        Assert.Equal("HEADER", head.Kind);
        Assert.Equal(CoreConstants.DefaultHeadSize, head.Length);
        Assert.Equal(64 * 1024, head.Length);
    }

    [Fact]
    public void HeadRegion_HonorsConfiguredSize()
    {
        string file = Path.Combine(_testDir, "small_head.png");
        File.WriteAllBytes(file, FakePng(200_000));

        var obf = new FileObfuscator().ObfuscateFile(file, MakeConfig(4096));
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        Assert.Equal(4096, read.Metadata.LockRegions[0].Length);
    }

    [Fact]
    public void HeadRegion_ClampsOutOfRangeConfig()
    {
        string file = Path.Combine(_testDir, "clamp.png");
        File.WriteAllBytes(file, FakePng(200_000));

        var obf = new FileObfuscator().ObfuscateFile(file, MakeConfig(0));   // 非法值 → 取 MinHeadSize
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        Assert.Equal(CoreConstants.MinHeadSize, read.Metadata.LockRegions[0].Length);
    }

    [Fact]
    public void SmallFile_IsEncryptedEntirely()
    {
        string file = Path.Combine(_testDir, "tiny.png");
        byte[] original = FakePng(500);
        File.WriteAllBytes(file, original);
        string hash = Convert.ToHexString(SHA256.HashData(original));

        var obf = new FileObfuscator().ObfuscateFile(file, MakeConfig());
        Assert.True(obf.Success, obf.ErrorMessage);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        Assert.Single(read.Metadata.LockRegions);
        Assert.Equal(original.Length, read.Metadata.LockRegions[0].Length);

        var rest = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Fact]
    public void HeadMerge_TakesMaxExtent_NotLastRegion()
    {
        // A 区(16,+8000) 尾部 8016 超出头部区 4096；B 区(33,+10) 更短且落在头部区内。
        // 合并必须取最大覆盖 8016，不能被 B 回缩到 4096。
        string file = Path.Combine(_testDir, "merge.png");
        byte[] original = FakePng(20_000);
        File.WriteAllBytes(file, original);

        var cfg = MakeConfig();
        byte[] mk = cfg.UnwrapMasterKeyWithPin("pin-123456");
        byte[] salt = Convert.FromBase64String(cfg.ConfigSalt!);

        var meta = new V2Metadata
        {
            OriginalFileName = "merge.png",
            OriginalExtension = ".png",
            LockRegions = new List<LockRegion>
            {
                new LockRegion(16, 8000, "A-LONG"),
                new LockRegion(33, 10, "B-SHORT")
            }
        };
        V2Tail.ProtectInPlace(file, meta, "pin-123456", mk, salt, headSize: 4096);

        var read = V2Tail.ReadFooter(file, "pin-123456", null);
        Assert.Single(read.Metadata.LockRegions);
        Assert.Equal(8016, read.Metadata.LockRegions[0].Length);

        var rest = new FileObfuscator().RestoreFile(file, "pin-123456");
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(300 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public void PngLocator_FindsIdat_EvenWhenChunkIsLarge(int idatLength)
    {
        string file = Path.Combine(_testDir, $"idat_{idatLength}.png");
        using (var ms = new MemoryStream())
        {
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            ms.Write(new byte[] { 0, 0, 0, 13 });
            ms.Write(Encoding.ASCII.GetBytes("IHDR"));
            ms.Write(new byte[13]);
            ms.Write(new byte[4]);
            byte[] len = BitConverter.GetBytes(idatLength);
            Array.Reverse(len);
            ms.Write(len);
            ms.Write(Encoding.ASCII.GetBytes("IDAT"));
            ms.Write(new byte[idatLength]);
            ms.Write(new byte[4]);
            File.WriteAllBytes(file, ms.ToArray());
        }

        var regions = StructureLocator.Locate(file, ".png");
        Assert.Contains(regions, r => r.Kind == "IDAT");
        Assert.Contains(regions, r => r.Kind == "IHDR");
    }
}
