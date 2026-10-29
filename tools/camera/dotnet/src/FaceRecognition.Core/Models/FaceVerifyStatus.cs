namespace FaceRecognition.Core.Models;

/// <summary>
/// 人脸身份认证结果状态枚举
/// </summary>
public enum FaceVerifyStatus
{
    /// <summary>认证通过（检测到人脸且与特征库匹配达标）</summary>
    Match,

    /// <summary>认证失败（检测到清晰人脸，但相似度低于门限，非本人）</summary>
    Mismatch,

    /// <summary>未检测到人脸（超时窗口内未捕获到有效人脸）</summary>
    NoFace,

    /// <summary>硬件异常（未找到可用摄像头或设备被独占占用）</summary>
    NoCamera,

    /// <summary>图像无效（提供的图像数据无法解码或已损坏）</summary>
    InvalidImage
}
