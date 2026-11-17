using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HideFiles.Core;
using Xunit;

namespace HideFiles.Tests;

public class BatchAndIntegrationTests : IDisposable
{
    private readonly string _testDir;

    public BatchAndIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "HideFiles_Batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void BatchProcessor_MultiFolderBatch_ObfuscateAndRestore100Percent()
    {
        var config = HideFilesConfig.CreateDefault();
        config.IncludeSubdirectories = true;
        config.SetPinV2("batch-pin");
        config.ProtectionMode = ProtectionMode.FullEncrypt; // 合成样本无真实媒体结构
        var processor = new BatchProcessor();

        // 构造深层嵌套文件结构
        string sub1 = Path.Combine(_testDir, "Videos");
        string sub2 = Path.Combine(_testDir, "Images", "2026");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);

        var originalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 创建 10 个视频文件
        for (int i = 0; i < 10; i++)
        {
            string p = Path.Combine(sub1, $"vid_{i:D2}.mp4");
            byte[] bytes = Encoding.UTF8.GetBytes($"VIDEO_PAYLOAD_TEST_DATA_BLOCK_{i}_" + new string('A', 5000));
            File.WriteAllBytes(p, bytes);
            originalHashes[p] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        // 创建 10 个图片文件
        for (int i = 0; i < 10; i++)
        {
            string p = Path.Combine(sub2, $"img_{i:D2}.png");
            byte[] bytes = Encoding.UTF8.GetBytes($"IMAGE_PAYLOAD_TEST_DATA_BLOCK_{i}_" + new string('B', 3000));
            File.WriteAllBytes(p, bytes);
            originalHashes[p] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        // 1. 扫描待混淆文件
        var collectedForHide = processor.CollectFiles(new[] { _testDir }, config, forObfuscate: true);
        Assert.Equal(20, collectedForHide.Count);

        // 2. 批量混淆
        var hideProgressList = new List<double>();
        var hideProgress = new Progress<BatchProgress>(p => hideProgressList.Add(p.ProgressPercentage));

        var hideSummary = processor.ObfuscateBatch(collectedForHide, config, hideProgress);
        Assert.Equal(20, hideSummary.Total);
        Assert.Equal(20, hideSummary.Succeeded);
        Assert.Equal(0, hideSummary.Failed);

        // 验证文件扩展名已按规则改变（mp4 -> dat, png -> tmp）
        var datFiles = Directory.GetFiles(_testDir, "*.dat", SearchOption.AllDirectories);
        var tmpFiles = Directory.GetFiles(_testDir, "*.tmp", SearchOption.AllDirectories);
        Assert.Equal(10, datFiles.Length);
        Assert.Equal(10, tmpFiles.Length);

        // 3. 扫描混淆后文件并批量还原
        var collectedForRestore = processor.CollectFiles(new[] { _testDir }, config, forObfuscate: false);
        Assert.Equal(20, collectedForRestore.Count);

        var restoreSummary = processor.RestoreBatch(collectedForRestore, "batch-pin");
        Assert.Equal(20, restoreSummary.Total);
        Assert.Equal(20, restoreSummary.Succeeded);
        Assert.Equal(0, restoreSummary.Failed);

        // 4. 验证全部无损恢复且哈希 100% 一致
        foreach (var kv in originalHashes)
        {
            Assert.True(File.Exists(kv.Key), $"Restored file missing: {kv.Key}");
            byte[] currentBytes = File.ReadAllBytes(kv.Key);
            string currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));
            Assert.Equal(kv.Value, currentHash);
        }
    }
}
