using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

/// <summary>
/// 目录名混淆：可逆、幂等（不能重复应用）、冲突不覆盖、超长名明确报错。
/// </summary>
public class DirectoryNameTests : IDisposable
{
    private readonly string _testDir;

    public DirectoryNameTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    [Fact]
    public void ObfuscateName_IsIdempotent()
    {
        string once = DirectoryObfuscator.ObfuscateName("我的私密相册");
        string twice = DirectoryObfuscator.ObfuscateName(once);

        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, once);
        Assert.Equal(once, twice);      // 已混淆的目录名原样返回，不会二次混淆
    }

    [Fact]
    public void ObfuscateDirectory_SecondRun_RenamesNothing()
    {
        string root = Path.Combine(_testDir, "root");
        Directory.CreateDirectory(Path.Combine(root, "Photos", "2026"));

        var first = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(first.Success, string.Join(";", first.Failures));
        Assert.Equal(2, first.Renamed);
        Assert.Equal(0, first.Skipped);

        var second = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(second.Success, string.Join(";", second.Failures));
        Assert.Equal(0, second.Renamed);        // 幂等：第二次不做任何改名
        Assert.Equal(2, second.Skipped);

        Assert.Single(Directory.GetDirectories(root));
        Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, Path.GetFileName(Directory.GetDirectories(root)[0]));
    }

    [Fact]
    public void ObfuscateDirectory_RoundTripsNestedTree()
    {
        string root = Path.Combine(_testDir, "tree");
        string deep = Path.Combine(root, "A", "B", "C");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "note.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "top.txt"), "top");

        var obf = DirectoryObfuscator.ObfuscateDirectory(root, recursive: true);
        Assert.True(obf.Success, string.Join(";", obf.Failures));
        Assert.Equal(3, obf.Renamed);
        Assert.False(Directory.Exists(Path.Combine(root, "A")));

        var allObfuscated = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        Assert.All(allObfuscated, d =>
            Assert.StartsWith(CoreConstants.ObfuscatedDirectoryPrefix, Path.GetFileName(d)));

        var rest = DirectoryObfuscator.RestoreDirectory(root, recursive: true);
        Assert.True(rest.Success, string.Join(";", rest.Failures));
        Assert.Equal(3, rest.Renamed);

        Assert.True(Directory.Exists(deep));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(deep, "note.txt")));
        Assert.Equal("top", File.ReadAllText(Path.Combine(root, "top.txt")));
    }

    [Fact]
    public void RestoreDirectory_DoesNotOverwriteExistingName()
    {
        string root = Path.Combine(_testDir, "clash");
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "Alpha", "a.txt"), "original");

        var obf = DirectoryObfuscator.ObfuscateDirectory(root);
        Assert.True(obf.Success, string.Join(";", obf.Failures));

        // 用户之后又建了一个同名目录
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "Alpha", "a.txt"), "later");

        var rest = DirectoryObfuscator.RestoreDirectory(root);
        Assert.True(rest.Success, string.Join(";", rest.Failures));

        Assert.Equal("later", File.ReadAllText(Path.Combine(root, "Alpha", "a.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "Alpha (1)", "a.txt")));
    }

    [Fact]
    public void ObfuscateName_TooLong_Throws()
    {
        string longName = new string('目', 200);   // UTF-8 600 字节 → Base32 远超上限
        var ex = Assert.Throws<InvalidOperationException>(() => DirectoryObfuscator.ObfuscateName(longName));
        Assert.Contains("过长", ex.Message);
    }

    [Fact]
    public void RestoreName_RejectsNonObfuscatedAndBrokenNames()
    {
        Assert.False(DirectoryObfuscator.TryRestoreName("普通目录", out _));
        Assert.False(DirectoryObfuscator.TryRestoreName(CoreConstants.ObfuscatedDirectoryPrefix, out _));
        Assert.False(DirectoryObfuscator.TryRestoreName(CoreConstants.ObfuscatedDirectoryPrefix + "!!!!", out _));
    }
}
