using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

public class FileNameTests : IDisposable
{
    private readonly string _testDir;

    public FileNameTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_File_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    [Fact]
    public void ObfuscateFileName_WholeName_IsIdempotent()
    {
        string original = "重要文档.docx";
        string once = FileNameObfuscator.ObfuscateFileName(original, keepExtension: false);
        string twice = FileNameObfuscator.ObfuscateFileName(once, keepExtension: false);

        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, once);
        Assert.Equal(once, twice);
        Assert.True(FileNameObfuscator.IsObfuscatedFileName(once));
    }

    [Fact]
    public void ObfuscateFileName_KeepExtension_IsIdempotent()
    {
        string original = "重要文档.docx";
        string once = FileNameObfuscator.ObfuscateFileName(original, keepExtension: true);
        string twice = FileNameObfuscator.ObfuscateFileName(once, keepExtension: true);

        Assert.EndsWith(".docx", once);
        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, once);
        Assert.Equal(once, twice);
        Assert.True(FileNameObfuscator.IsObfuscatedFileName(once));
    }

    [Fact]
    public void TryRestoreFileName_WholeName_MatchesOriginal()
    {
        string[] testNames = { "report.pdf", "家庭照片.jpg", "notes.txt", "README", "v1.0.0.tar.gz" };
        foreach (string name in testNames)
        {
            string obfuscated = FileNameObfuscator.ObfuscateFileName(name, keepExtension: false);
            Assert.True(FileNameObfuscator.TryRestoreFileName(obfuscated, out string restored));
            Assert.Equal(name, restored);
        }
    }

    [Fact]
    public void TryRestoreFileName_KeepExtension_MatchesOriginal()
    {
        string[] testNames = { "report.pdf", "家庭照片.jpg", "notes.txt", "v1.0.0.tar.gz" };
        foreach (string name in testNames)
        {
            string obfuscated = FileNameObfuscator.ObfuscateFileName(name, keepExtension: true);
            Assert.True(FileNameObfuscator.TryRestoreFileName(obfuscated, out string restored));
            Assert.Equal(name, restored);
        }
    }

    [Fact]
    public void ObfuscateDirectoryFiles_RoundTripsNestedFiles()
    {
        string root = Path.Combine(_testDir, "nested");
        Directory.CreateDirectory(Path.Combine(root, "sub1"));
        Directory.CreateDirectory(Path.Combine(root, "sub2"));

        string f1 = Path.Combine(root, "file1.txt");
        string f2 = Path.Combine(root, "sub1", "file2.jpg");
        string f3 = Path.Combine(root, "sub2", "file3.mp4");
        File.WriteAllText(f1, "hello 1");
        File.WriteAllText(f2, "hello 2");
        File.WriteAllText(f3, "hello 3");

        // 混淆全部文件
        var obfResult = FileNameObfuscator.ObfuscateDirectoryFiles(root, recursive: true, keepExtension: false);
        Assert.True(obfResult.Success, string.Join(";", obfResult.Failures));
        Assert.Equal(3, obfResult.Renamed);
        Assert.Equal(0, obfResult.Skipped);

        // 再次混淆应全部跳过
        var obfAgain = FileNameObfuscator.ObfuscateDirectoryFiles(root, recursive: true, keepExtension: false);
        Assert.Equal(0, obfAgain.Renamed);
        Assert.Equal(3, obfAgain.Skipped);

        // 还原全部文件
        var restoreResult = FileNameObfuscator.RestoreDirectoryFiles(root, recursive: true);
        Assert.True(restoreResult.Success, string.Join(";", restoreResult.Failures));
        Assert.Equal(3, restoreResult.Renamed);
        Assert.Equal(0, restoreResult.Skipped);

        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));
        Assert.True(File.Exists(f3));
        Assert.Equal("hello 1", File.ReadAllText(f1));
        Assert.Equal("hello 2", File.ReadAllText(f2));
        Assert.Equal("hello 3", File.ReadAllText(f3));
    }

    [Fact]
    public void ObfuscateFiles_SpecifiedList_RoundTrips()
    {
        string f1 = Path.Combine(_testDir, "docA.txt");
        string f2 = Path.Combine(_testDir, "docB.txt");
        File.WriteAllText(f1, "A");
        File.WriteAllText(f2, "B");

        var obf = FileNameObfuscator.ObfuscateFiles(new[] { f1, f2 }, keepExtension: true);
        Assert.True(obf.Success);
        Assert.Equal(2, obf.Renamed);

        var curFiles = Directory.GetFiles(_testDir);
        var rest = FileNameObfuscator.RestoreFiles(curFiles);
        Assert.True(rest.Success);
        Assert.Equal(2, rest.Renamed);

        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));
    }

    [Fact]
    public void TryRestoreFileName_TamperedCrc_Fails()
    {
        string original = "秘密文件.xlsx";
        string obf = FileNameObfuscator.ObfuscateFileName(original, keepExtension: false);
        Assert.True(FileNameObfuscator.TryRestoreFileName(obf, out _));

        // 篡改最后一位字符
        char last = obf[^1];
        char replacement = last == 'A' ? 'B' : 'A';
        string tampered = obf[..^1] + replacement;

        Assert.False(FileNameObfuscator.TryRestoreFileName(tampered, out _));
    }

    [Fact]
    public void ObfuscateFileName_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => FileNameObfuscator.ObfuscateFileName(""));
        Assert.Throws<ArgumentException>(() => FileNameObfuscator.ObfuscateFileName("   "));
    }
}
