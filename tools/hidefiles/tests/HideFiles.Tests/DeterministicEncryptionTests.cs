using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

public class DeterministicEncryptionTests : IDisposable
{
    private readonly string _testDir;

    public DeterministicEncryptionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_DetTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    private byte[] CreateSamplePng()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        ms.Write(new byte[] { 0, 0, 0, 13 });
        ms.Write(Encoding.ASCII.GetBytes("IHDR"));
        ms.Write(RandomNumberGenerator.GetBytes(13));
        ms.Write(RandomNumberGenerator.GetBytes(4));
        ms.Write(RandomNumberGenerator.GetBytes(4096));
        return ms.ToArray();
    }

    [Fact]
    public void FullEncrypt_Deterministic_ProducesBitForBitIdenticalOutput()
    {
        string pin = "DetSecretPin987";
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin(pin);
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        config.Deterministic = true;
        config.ObfuscateFileName = false;

        byte[] content = Encoding.UTF8.GetBytes("This is a deterministic test content for CarroCasket encryption! Repeat 1234567890.");
        string dir1 = Path.Combine(_testDir, "run1");
        string dir2 = Path.Combine(_testDir, "run2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        string file1 = Path.Combine(dir1, "sample.dat");
        string file2 = Path.Combine(dir2, "sample.dat");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        var obfuscator = new FileObfuscator();
        var res1 = obfuscator.ObfuscateFile(file1, config);
        var res2 = obfuscator.ObfuscateFile(file2, config);

        Assert.True(res1.Success, res1.ErrorMessage);
        Assert.True(res2.Success, res2.ErrorMessage);

        byte[] enc1 = File.ReadAllBytes(res1.FinalPath);
        byte[] enc2 = File.ReadAllBytes(res2.FinalPath);

        string hash1 = Convert.ToHexString(SHA256.HashData(enc1));
        string hash2 = Convert.ToHexString(SHA256.HashData(enc2));

        Assert.Equal(hash1, hash2);
        Assert.Equal(enc1, enc2);

        // 验证还原
        var rest = obfuscator.RestoreFile(res1.FinalPath, pin);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(content, File.ReadAllBytes(rest.FinalPath));
    }

    [Fact]
    public void StructureLock_Deterministic_ProducesBitForBitIdenticalOutput()
    {
        string pin = "DetSecretPin987";
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin(pin);
        config.ProtectionMode = ProtectionMode.StructureLock;
        config.Deterministic = true;
        config.ObfuscateFileName = false;

        byte[] pngBytes = CreateSamplePng();
        string dir1 = Path.Combine(_testDir, "struct_run1");
        string dir2 = Path.Combine(_testDir, "struct_run2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        string file1 = Path.Combine(dir1, "sample.png");
        string file2 = Path.Combine(dir2, "sample.png");
        File.WriteAllBytes(file1, pngBytes);
        File.WriteAllBytes(file2, pngBytes);

        var obfuscator = new FileObfuscator();
        var res1 = obfuscator.ObfuscateFile(file1, config);
        var res2 = obfuscator.ObfuscateFile(file2, config);

        Assert.True(res1.Success, res1.ErrorMessage);
        Assert.True(res2.Success, res2.ErrorMessage);

        byte[] enc1 = File.ReadAllBytes(res1.FinalPath);
        byte[] enc2 = File.ReadAllBytes(res2.FinalPath);

        string hash1 = Convert.ToHexString(SHA256.HashData(enc1));
        string hash2 = Convert.ToHexString(SHA256.HashData(enc2));

        Assert.Equal(hash1, hash2);
        Assert.Equal(enc1, enc2);

        // 验证还原
        var rest = obfuscator.RestoreFile(res1.FinalPath, pin);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(pngBytes, File.ReadAllBytes(rest.FinalPath));
    }

    [Fact]
    public void CrossInstance_DifferentConfigs_ProducesBitForBitIdenticalOutput()
    {
        string pin = "CrossMachinePin456";

        // 模拟两台不同的电脑（随机初始化的两个 config 实例，拥有不同的随机 ConfigSalt 与 MasterKey）
        var configA = HideFilesConfig.CreateDefault();
        configA.ChangePin(pin);
        configA.Deterministic = true;
        configA.ProtectionMode = ProtectionMode.FullEncrypt;
        configA.ObfuscateFileName = false;

        var configB = HideFilesConfig.CreateDefault();
        configB.ChangePin(pin);
        configB.Deterministic = true;
        configB.ProtectionMode = ProtectionMode.FullEncrypt;
        configB.ObfuscateFileName = false;

        // 确认两台机器本地的随机 ConfigSalt 确实不同
        Assert.NotEqual(configA.ConfigSalt, configB.ConfigSalt);

        byte[] content = Encoding.UTF8.GetBytes("Cross-machine deterministic verification data.");
        string dirA = Path.Combine(_testDir, "machine_a");
        string dirB = Path.Combine(_testDir, "machine_b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string fileA = Path.Combine(dirA, "sample.dat");
        string fileB = Path.Combine(dirB, "sample.dat");
        File.WriteAllBytes(fileA, content);
        File.WriteAllBytes(fileB, content);

        var obfuscator = new FileObfuscator();
        var resA = obfuscator.ObfuscateFile(fileA, configA);
        var resB = obfuscator.ObfuscateFile(fileB, configB);

        Assert.True(resA.Success, resA.ErrorMessage);
        Assert.True(resB.Success, resB.ErrorMessage);

        byte[] encA = File.ReadAllBytes(resA.FinalPath);
        byte[] encB = File.ReadAllBytes(resB.FinalPath);

        Assert.Equal(encA, encB);

        // 用 machine B 的 PIN 还原由 machine A 生成的确定性文件
        var rest = obfuscator.RestoreFile(resA.FinalPath, pin);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal(content, File.ReadAllBytes(rest.FinalPath));
    }

    [Fact]
    public void NonDeterministic_ProducesDifferentOutputsByDefault()
    {
        string pin = "RandomSecret123";
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin(pin);
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        config.Deterministic = false; // 默认随机模式
        config.ObfuscateFileName = false;

        byte[] content = Encoding.UTF8.GetBytes("Testing non-deterministic default behavior.");
        string file1 = Path.Combine(_testDir, "rand1.dat");
        string file2 = Path.Combine(_testDir, "rand2.dat");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        var obfuscator = new FileObfuscator();
        var res1 = obfuscator.ObfuscateFile(file1, config);
        var res2 = obfuscator.ObfuscateFile(file2, config);

        Assert.True(res1.Success, res1.ErrorMessage);
        Assert.True(res2.Success, res2.ErrorMessage);

        byte[] enc1 = File.ReadAllBytes(res1.FinalPath);
        byte[] enc2 = File.ReadAllBytes(res2.FinalPath);

        // 随机模式下每次加密输出应不同
        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void DifferentFiles_ProduceDifferentOutputs_EvenWithSameLengthAndPin()
    {
        string pin = "DetSecretPin987";
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin(pin);
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        config.Deterministic = true;
        config.ObfuscateFileName = false;

        byte[] content1 = Encoding.UTF8.GetBytes("Same length payload AAAAAAAAAAAAAAAAA");
        byte[] content2 = Encoding.UTF8.GetBytes("Same length payload BBBBBBBBBBBBBBBBB");
        string file1 = Path.Combine(_testDir, "diff1.dat");
        string file2 = Path.Combine(_testDir, "diff2.dat");
        File.WriteAllBytes(file1, content1);
        File.WriteAllBytes(file2, content2);

        var obfuscator = new FileObfuscator();
        var res1 = obfuscator.ObfuscateFile(file1, config);
        var res2 = obfuscator.ObfuscateFile(file2, config);

        Assert.True(res1.Success, res1.ErrorMessage);
        Assert.True(res2.Success, res2.ErrorMessage);

        byte[] enc1 = File.ReadAllBytes(res1.FinalPath);
        byte[] enc2 = File.ReadAllBytes(res2.FinalPath);

        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void ObfuscateFileName_True_Deterministic_ProducesSameNameAndSameBytes()
    {
        string pin = "DetNamePin123";
        var config = HideFilesConfig.CreateDefault();
        config.ChangePin(pin);
        config.ProtectionMode = ProtectionMode.FullEncrypt;
        config.Deterministic = true;
        config.ObfuscateFileName = true; // 开启文件名混淆

        byte[] content = Encoding.UTF8.GetBytes("Content with obfuscated target file name!");
        string dir1 = Path.Combine(_testDir, "name_run1");
        string dir2 = Path.Combine(_testDir, "name_run2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        string file1 = Path.Combine(dir1, "data.txt");
        string file2 = Path.Combine(dir2, "data.txt");
        File.WriteAllBytes(file1, content);
        File.WriteAllBytes(file2, content);

        var obfuscator = new FileObfuscator();
        var res1 = obfuscator.ObfuscateFile(file1, config);
        var res2 = obfuscator.ObfuscateFile(file2, config);

        Assert.True(res1.Success, res1.ErrorMessage);
        Assert.True(res2.Success, res2.ErrorMessage);

        // 生成的目标文件名应当完全一致
        Assert.Equal(Path.GetFileName(res1.FinalPath), Path.GetFileName(res2.FinalPath));
        // 生成的目标密文字节应当完全一致
        Assert.Equal(File.ReadAllBytes(res1.FinalPath), File.ReadAllBytes(res2.FinalPath));

        // 还原后恢复原始文件名与原始内容
        var rest = obfuscator.RestoreFile(res1.FinalPath, pin);
        Assert.True(rest.Success, rest.ErrorMessage);
        Assert.Equal("data.txt", Path.GetFileName(rest.FinalPath));
        Assert.Equal(content, File.ReadAllBytes(rest.FinalPath));
    }
}
