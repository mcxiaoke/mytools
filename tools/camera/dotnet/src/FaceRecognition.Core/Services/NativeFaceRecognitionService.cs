using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FaceRecognition.Core.Models;
using OpenCvSharp;

namespace FaceRecognition.Core.Services;

/// <summary>
/// 基于 OpenCvSharp5 原生 ONNX 推理的高性能人脸核验服务
/// </summary>
public sealed class NativeFaceRecognitionService : IFaceRecognitionService
{
    private FaceDetectorYN? _detector;
    private FaceRecognizerSF? _recognizer;
    private FaceFeatureDatabase? _database;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsInitialized => _detector != null && _recognizer != null && _database != null;
    public string UserName => _database?.UserName ?? string.Empty;
    public int EnrolledSampleCount => _database?.Count ?? 0;

    /// <summary>
    /// 初始化检测器、识别器及多姿态特征库
    /// </summary>
    public void Initialize(string yunetPath, string sfacePath, string featureJsonPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(yunetPath)) throw new FileNotFoundException("未找到 YuNet 检测模型文件", yunetPath);
        if (!File.Exists(sfacePath)) throw new FileNotFoundException("未找到 SFace 识别模型文件", sfacePath);
        if (!File.Exists(featureJsonPath)) throw new FileNotFoundException("未找到特征库 JSON 文件", featureJsonPath);

        lock (_lock)
        {
            _detector?.Dispose();
            _recognizer?.Dispose();

            // 载入 ONNX 模型（YuNet 默认 640x480 分辨率）
            _detector = FaceDetectorYN.Create(yunetPath, "", new Size(640, 480), 0.75f, 0.3f);
            _recognizer = FaceRecognizerSF.Create(sfacePath, "");

            // 载入特征底库
            _database = FaceFeatureDatabase.LoadFromFile(featureJsonPath);
        }
    }

    /// <summary>
    /// CLAHE 亮度通道自适应直方图均衡化（改善逆光、暗光表现）
    /// </summary>
    public static Mat EnhanceIllumination(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        var channels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
            clahe.Apply(channels[0], channels[0]);

            using var merged = new Mat();
            Cv2.Merge(channels, merged);

            var enhanced = new Mat();
            Cv2.CvtColor(merged, enhanced, ColorConversionCodes.Lab2BGR);
            return enhanced;
        }
        finally
        {
            foreach (var ch in channels)
            {
                ch.Dispose();
            }
        }
    }

    /// <summary>
    /// 从 1x128 Mat 特征矩阵转换为 float[128]
    /// </summary>
    private static float[] MatToFloatArray128(Mat mat)
    {
        float[] arr = new float[128];
        for (int i = 0; i < 128; i++)
        {
            arr[i] = mat.At<float>(0, i);
        }
        return arr;
    }

    /// <summary>
    /// 调起指定本地摄像头，在时间窗口内进行实时动态核验
    /// </summary>
    public async Task<FaceVerifyResult> VerifyCameraAsync(
        float timeoutSeconds = 3.0f,
        float threshold = 0.38f,
        int cameraId = 0,
        int minFaceHeight = 80,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();

            // 在 Windows 优先使用 DirectShow 后端以获得低延迟与稳定访问
            VideoCapture? cap = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    cap = new VideoCapture(cameraId, VideoCaptureAPIs.DSHOW);
                }
                if (cap == null || !cap.IsOpened())
                {
                    cap?.Dispose();
                    cap = new VideoCapture(cameraId);
                }

                if (!cap.IsOpened())
                {
                    return new FaceVerifyResult
                    {
                        Status = FaceVerifyStatus.NoCamera,
                        UserName = UserName,
                        Threshold = threshold,
                        ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                        AttemptsCount = 0,
                        Message = $"无法打开本地摄像头设备 (ID: {cameraId})，设备可能未连接或已被独占占用。"
                    };
                }

                // 快速预热 3 帧以稳定自动曝光 (AE)
                using var dummy = new Mat();
                for (int i = 0; i < 3; i++)
                {
                    cap.Read(dummy);
                }

                int attempts = 0;
                float highestScore = 0f;
                string? bestMatchedSample = null;
                bool faceEverDetected = false;

                using var frame = new Mat();
                while (sw.Elapsed.TotalSeconds < timeoutSeconds && !cancellationToken.IsCancellationRequested)
                {
                    if (!cap.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(30);
                        continue;
                    }

                    attempts++;
                    var singleResult = VerifyImageInternal(frame, threshold, minFaceHeight, sw.Elapsed.TotalSeconds, attempts);

                    if (singleResult.Status != FaceVerifyStatus.NoFace)
                    {
                        faceEverDetected = true;
                        if (singleResult.Confidence > highestScore)
                        {
                            highestScore = singleResult.Confidence;
                            bestMatchedSample = singleResult.BestSample;
                        }

                        // 任意一次匹配通过策略：立即快速熔断返回！
                        if (singleResult.Matched)
                        {
                            return singleResult;
                        }
                    }

                    Thread.Sleep(60); // 间隔约 60ms 采样下一帧
                }

                double totalCost = Math.Round(sw.Elapsed.TotalSeconds, 2);
                if (!faceEverDetected)
                {
                    return new FaceVerifyResult
                    {
                        Status = FaceVerifyStatus.NoFace,
                        UserName = UserName,
                        Threshold = threshold,
                        ElapsedSeconds = totalCost,
                        AttemptsCount = attempts,
                        Message = $"在 {timeoutSeconds:F1} 秒超时时间内未检测到清晰人脸，请端正坐姿正视摄像头重试。"
                    };
                }
                else
                {
                    return new FaceVerifyResult
                    {
                        Status = FaceVerifyStatus.Mismatch,
                        UserName = UserName,
                        Confidence = highestScore,
                        Threshold = threshold,
                        BestSample = bestMatchedSample,
                        ElapsedSeconds = totalCost,
                        AttemptsCount = attempts,
                        Message = $"人脸特征相似度不足 (最高得分: {highestScore:F3} < 门限 {threshold:F3})，非注册操作人！"
                    };
                }
            }
            finally
            {
                cap?.Dispose();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 对单张 OpenCV Mat 图像进行静态核验
    /// </summary>
    public FaceVerifyResult VerifyImage(Mat image, float threshold = 0.38f, int minFaceHeight = 80)
    {
        EnsureInitialized();
        var sw = Stopwatch.StartNew();
        return VerifyImageInternal(image, threshold, minFaceHeight, sw.Elapsed.TotalSeconds, 1);
    }

    /// <summary>
    /// 对本地图片文件路径进行静态核验
    /// </summary>
    public FaceVerifyResult VerifyImage(string imagePath, float threshold = 0.38f, int minFaceHeight = 80)
    {
        EnsureInitialized();
        if (!File.Exists(imagePath))
        {
            return new FaceVerifyResult
            {
                Status = FaceVerifyStatus.InvalidImage,
                UserName = UserName,
                Threshold = threshold,
                Message = $"指定的图片文件不存在: {imagePath}"
            };
        }

        using var img = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (img.Empty())
        {
            return new FaceVerifyResult
            {
                Status = FaceVerifyStatus.InvalidImage,
                UserName = UserName,
                Threshold = threshold,
                Message = $"无法从路径解码图片数据: {imagePath}"
            };
        }

        return VerifyImage(img, threshold, minFaceHeight);
    }

    /// <summary>
    /// 对内存图片二进制数据 (JPG/PNG) 进行静态核验
    /// </summary>
    public FaceVerifyResult VerifyImage(byte[] imageBytes, float threshold = 0.38f, int minFaceHeight = 80)
    {
        EnsureInitialized();
        if (imageBytes == null || imageBytes.Length == 0)
        {
            return new FaceVerifyResult
            {
                Status = FaceVerifyStatus.InvalidImage,
                UserName = UserName,
                Threshold = threshold,
                Message = "提供的图像二进制字节数组为空。"
            };
        }

        using var img = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (img.Empty())
        {
            return new FaceVerifyResult
            {
                Status = FaceVerifyStatus.InvalidImage,
                UserName = UserName,
                Threshold = threshold,
                Message = "提供的二进制字节数据无法解码为有效图像。"
            };
        }

        return VerifyImage(img, threshold, minFaceHeight);
    }

    /// <summary>
    /// 快速检查摄像头硬件是否可用
    /// </summary>
    public bool CheckCameraAvailable(int cameraId = 0)
    {
        try
        {
            VideoCapture? cap = null;
            if (OperatingSystem.IsWindows())
            {
                cap = new VideoCapture(cameraId, VideoCaptureAPIs.DSHOW);
            }
            if (cap == null || !cap.IsOpened())
            {
                cap?.Dispose();
                cap = new VideoCapture(cameraId);
            }

            bool available = cap.IsOpened();
            cap.Dispose();
            return available;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 内部单帧人脸推理与比对核心逻辑
    /// </summary>
    private FaceVerifyResult VerifyImageInternal(Mat original, float threshold, int minFaceHeight, double elapsed, int attempts)
    {
        using var enhanced = EnhanceIllumination(original);

        lock (_lock)
        {
            _detector!.SetInputSize(enhanced.Width, enhanced.Height);
            using var faces = new Mat();
            _detector.Detect(enhanced, faces);

            if (faces.Rows == 0)
            {
                return new FaceVerifyResult
                {
                    Status = FaceVerifyStatus.NoFace,
                    UserName = UserName,
                    Confidence = 0f,
                    Threshold = threshold,
                    ElapsedSeconds = Math.Round(elapsed, 2),
                    AttemptsCount = attempts,
                    Message = "图像中未检测到清晰人脸。"
                };
            }

            // 选取面积最大的人脸
            int bestIdx = 0;
            float maxArea = 0f;
            int faceRows = faces.Rows;
            for (int r = 0; r < faceRows; r++)
            {
                float w = faces.At<float>(r, 2);
                float h = faces.At<float>(r, 3);
                float area = w * h;
                if (area > maxArea)
                {
                    maxArea = area;
                    bestIdx = r;
                }
            }

            float faceH = faces.At<float>(bestIdx, 3);
            if (faceH < minFaceHeight)
            {
                return new FaceVerifyResult
                {
                    Status = FaceVerifyStatus.NoFace,
                    UserName = UserName,
                    Confidence = 0f,
                    Threshold = threshold,
                    ElapsedSeconds = Math.Round(elapsed, 2),
                    AttemptsCount = attempts,
                    Message = $"人脸尺寸过小 (高度 {faceH:F0}px < 最小限制 {minFaceHeight}px)，已过滤。"
                };
            }

            int bx = (int)faces.At<float>(bestIdx, 0);
            int by = (int)faces.At<float>(bestIdx, 1);
            int bw = (int)faces.At<float>(bestIdx, 2);
            int bh = (int)faces.At<float>(bestIdx, 3);
            var box = new Rect(bx, by, bw, bh);

            // 五官 5 点关键点仿射对齐摆正
            using var aligned = new Mat();
            using var faceRow = faces.Row(bestIdx);
            _recognizer!.AlignCrop(enhanced, faceRow, aligned);

            // 提取 128 维特征向量
            using var featMat = new Mat();
            _recognizer.Feature(aligned, featMat);

            float[] liveFeature = MatToFloatArray128(featMat);

            // 双轨相似度比对
            var (bestScore, bestSample, _) = _database!.Match(liveFeature);

            bool isMatch = bestScore >= threshold;
            return new FaceVerifyResult
            {
                Status = isMatch ? FaceVerifyStatus.Match : FaceVerifyStatus.Mismatch,
                UserName = UserName,
                Confidence = bestScore,
                Threshold = threshold,
                BestSample = bestSample,
                ElapsedSeconds = Math.Round(elapsed, 2),
                AttemptsCount = attempts,
                BoundingBox = box,
                Message = isMatch ? "身份核验通过，确认为本人。" : $"人脸相似度不足 ({bestScore:F3} < 门限 {threshold:F3})，非注册操作人！"
            };
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized)
        {
            throw new InvalidOperationException("服务尚未完成初始化，请先调用 Initialize() 载入模型与特征库！");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            _detector?.Dispose();
            _recognizer?.Dispose();
            _detector = null;
            _recognizer = null;
            _database = null;
            _disposed = true;
        }
    }
}
