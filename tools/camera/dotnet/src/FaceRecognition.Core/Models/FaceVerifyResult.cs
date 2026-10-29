using System.Text.Json.Serialization;
using OpenCvSharp;

namespace FaceRecognition.Core.Models;

/// <summary>
/// 人脸核验结果详细载荷
/// </summary>
public sealed class FaceVerifyResult
{
    /// <summary>认证状态枚举</summary>
    public FaceVerifyStatus Status { get; set; }

    /// <summary>是否确认为本人放行</summary>
    public bool Matched => Status == FaceVerifyStatus.Match;

    /// <summary>目标用户名</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>实测最高综合余弦相似度</summary>
    public float Confidence { get; set; }

    /// <summary>本次判定的余弦相似度门限</summary>
    public float Threshold { get; set; }

    /// <summary>命中的最佳匹配底库照片文件名</summary>
    public string? BestSample { get; set; }

    /// <summary>核验总耗时（秒）</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>采样帧数 / 比对尝试次数</summary>
    public int AttemptsCount { get; set; }

    /// <summary>人读说明消息</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>检出的人脸矩形范围（若检出）</summary>
    [JsonIgnore]
    public Rect? BoundingBox { get; set; }

    public override string ToString()
    {
        return $"[{Status}] Matched={Matched}, User={UserName}, Confidence={Confidence:F4}, Threshold={Threshold:F3}, BestSample={BestSample}, Cost={ElapsedSeconds:F2}s ({AttemptsCount} frames)";
    }
}
