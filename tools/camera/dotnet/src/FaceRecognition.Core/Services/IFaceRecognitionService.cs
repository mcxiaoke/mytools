using System;
using System.Threading;
using System.Threading.Tasks;
using FaceRecognition.Core.Models;
using OpenCvSharp;

namespace FaceRecognition.Core.Services;

/// <summary>
/// 人脸识别核心服务接口（跨平台 .NET 原生实现）
/// </summary>
public interface IFaceRecognitionService : IDisposable
{
    /// <summary>服务是否已完成模型与特征库的初始化</summary>
    bool IsInitialized { get; }

    /// <summary>当前特征库所归属的用户名称</summary>
    string UserName { get; }

    /// <summary>底库中包含的多姿态样本总数</summary>
    int EnrolledSampleCount { get; }

    /// <summary>
    /// 初始化检测模型、识别模型与特征库
    /// </summary>
    void Initialize(string yunetPath, string sfacePath, string featureJsonPath);

    /// <summary>
    /// 调起指定本地摄像头，在时间窗口内进行实时动态核验
    /// 策略：在超时窗口内，只要任意一次比对成功即视为通过并快速放行。
    /// </summary>
    Task<FaceVerifyResult> VerifyCameraAsync(
        float timeoutSeconds = 3.0f,
        float threshold = 0.38f,
        int cameraId = 0,
        int minFaceHeight = 80,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 对单张 OpenCV Mat 图像进行静态核验
    /// </summary>
    FaceVerifyResult VerifyImage(Mat image, float threshold = 0.38f, int minFaceHeight = 80);

    /// <summary>
    /// 对本地图片文件路径进行静态核验
    /// </summary>
    FaceVerifyResult VerifyImage(string imagePath, float threshold = 0.38f, int minFaceHeight = 80);

    /// <summary>
    /// 对内存图片二进制数据 (JPG/PNG) 进行静态核验
    /// </summary>
    FaceVerifyResult VerifyImage(byte[] imageBytes, float threshold = 0.38f, int minFaceHeight = 80);

    /// <summary>
    /// 快速检查摄像头硬件是否可用
    /// </summary>
    bool CheckCameraAvailable(int cameraId = 0);
}
