using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 定位器与 Inspect：JPEG 不被 EXIF 内假 SOS 误导、MKV 尾部 Cues 可定位、
/// 有主密钥时能显示容器里的原始文件名。
/// </summary>
public class LocatorAndInspectTests : IDisposable
{
    private readonly string _testDir;

    public LocatorAndInspectTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Locate_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void JpegLocator_StopsAtRealSos_NotAtFalseMarkerInsideAppSegment()
    {
        string file = Path.Combine(_testDir, "photo.jpg");
        int realSos;
        using (var ms = new MemoryStream())
        {
            ms.Write(new byte[] { 0xFF, 0xD8 });                 // SOI
            ms.Write(new byte[] { 0xFF, 0xE1, 0x00, 0x10 });     // APP1, 段长 16（含长度字段）
            byte[] app1 = new byte[14];
            app1[5] = 0xFF; app1[6] = 0xDA;                       // EXIF 里出现假 SOS 标记
            ms.Write(app1);
            ms.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00 });   // DQT
            realSos = (int)ms.Length;
            ms.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0 });
            ms.Write(new byte[1024]);
            File.WriteAllBytes(file, ms.ToArray());
        }

        var regions = StructureLocator.Locate(file, ".jpg");

        var region = Assert.Single(regions);
        Assert.Equal(2, region.Offset);
        Assert.Equal(realSos - 2, region.Length);   // 覆盖到真正的 SOS，而不是 EXIF 里的假标记
    }

    [Fact]
    public void MkvLocator_FindsCuesWrittenAtFileTail()
    {
        string file = Path.Combine(_testDir, "tail-cues.mkv");
        int total = 9 * 1024 * 1024;                  // 头部 8MB 窗口之外
        byte[] buf = new byte[total];
        int pos = total - 200;
        buf[pos] = 0x1C; buf[pos + 1] = 0x53; buf[pos + 2] = 0xBB; buf[pos + 3] = 0x6B;  // Cues
        buf[pos + 4] = 0x84;                          // 1 字节 vint，值 4
        buf[pos + 5] = 1; buf[pos + 6] = 2; buf[pos + 7] = 3; buf[pos + 8] = 4;
        File.WriteAllBytes(file, buf);

        var regions = StructureLocator.Locate(file, ".mkv");

        var cues = Assert.Single(regions, r => r.Kind == "Cues");
        Assert.Equal(4, cues.Length);
        Assert.Equal(pos + 5, cues.Offset);
    }

    [Fact]
    public void Inspect_WithMasterKey_ReportsOriginalName()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ProtectionMode = ProtectionMode.StructureLock;
        cfg.ObfuscateFileName = true;
        cfg.ChangePin("pin-123456");
        byte[] masterKey = cfg.UnwrapMasterKeyWithPin("pin-123456");

        string file = Path.Combine(_testDir, "photo.png");
        File.WriteAllBytes(file, FakePng());
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        var withoutKey = new FileObfuscator().Inspect(obf.FinalPath);
        Assert.Null(withoutKey.OriginalFileName);          // 无密钥时不解密，保持空

        var withKey = new FileObfuscator().Inspect(obf.FinalPath, masterKey);
        Assert.Equal("photo.png", withKey.OriginalFileName);
        Assert.Equal(".png", withKey.OriginalExtension);
        Assert.Equal(FileStatus.Obfuscated, withKey.Status);
    }

    [Fact]
    public void Inspect_WithWrongMasterKey_StillReportsStatus()
    {
        var cfg = HideFilesConfig.CreateDefault();
        cfg.ProtectionMode = ProtectionMode.FullEncrypt;
        cfg.ChangePin("pin-123456");

        string file = Path.Combine(_testDir, "secret.mp4");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(4000));
        var obf = new FileObfuscator().ObfuscateFile(file, cfg);
        Assert.True(obf.Success, obf.ErrorMessage);

        byte[] wrongKey = RandomNumberGenerator.GetBytes(CoreConstants.MasterKeySize);
        var insp = new FileObfuscator().Inspect(obf.FinalPath, wrongKey);

        Assert.Equal(FileStatus.Obfuscated, insp.Status);   // 解不出名字不影响状态判定
        Assert.Null(insp.OriginalFileName);
    }
}
