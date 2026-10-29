using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceRecognition.Core.Models;

/// <summary>
/// 人脸多姿态特征向量底库
/// </summary>
public sealed class FaceFeatureDatabase
{
    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = "Master";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("feature_dim")]
    public int FeatureDim { get; set; } = 128;

    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = new();

    [JsonPropertyName("mean_feature")]
    public List<float> MeanFeature { get; set; } = new();

    [JsonPropertyName("features")]
    public List<List<float>> Features { get; set; } = new();

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    // 内存中缓存的扁平化数组，优化访问速度
    [JsonIgnore]
    private float[]? _cachedMean;

    [JsonIgnore]
    private List<float[]>? _cachedSamples;

    /// <summary>
    /// 从 JSON 文件读取底库
    /// </summary>
    public static FaceFeatureDatabase LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("未找到特征底库 JSON 文件", filePath);

        string json = File.ReadAllText(filePath);
        var db = JsonSerializer.Deserialize<FaceFeatureDatabase>(json)
                 ?? throw new InvalidDataException("特征库 JSON 反序列化为空！");

        db.EnsureOptimized();
        return db;
    }

    /// <summary>
    /// 从字节流读取底库
    /// </summary>
    public static FaceFeatureDatabase LoadFromStream(Stream stream)
    {
        var db = JsonSerializer.Deserialize<FaceFeatureDatabase>(stream)
                 ?? throw new InvalidDataException("特征库 JSON 反序列化为空！");
        db.EnsureOptimized();
        return db;
    }

    private void EnsureOptimized()
    {
        _cachedMean = MeanFeature.ToArray();
        _cachedSamples = Features.Select(f => f.ToArray()).ToList();
    }

    /// <summary>
    /// 计算两个 128 维特征向量的余弦相似度
    /// </summary>
    public static float ComputeCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("特征向量维度不匹配！");

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
        return denom > 0f ? (dot / denom) : 0f;
    }

    /// <summary>
    /// 双轨比对：
    /// 1. 与底库中所有特定姿态样本计算余弦相似度并取最大值；
    /// 2. 与多姿态融合中心向量计算余弦相似度；
    /// 综合得分 = Max(最高单样本得分, 均值中心得分)。
    /// </summary>
    public (float BestScore, string BestSample, float CentroidScore) Match(ReadOnlySpan<float> liveFeature)
    {
        if (_cachedSamples == null || _cachedMean == null)
            EnsureOptimized();

        float maxSampleScore = -1f;
        string bestSample = string.Empty;

        for (int i = 0; i < _cachedSamples!.Count; i++)
        {
            float s = ComputeCosineSimilarity(liveFeature, _cachedSamples[i]);
            if (s > maxSampleScore)
            {
                maxSampleScore = s;
                bestSample = Sources[i];
            }
        }

        float centroidScore = ComputeCosineSimilarity(liveFeature, _cachedMean!);
        float bestScore = Math.Max(maxSampleScore, centroidScore);

        return (bestScore, bestSample, centroidScore);
    }
}
