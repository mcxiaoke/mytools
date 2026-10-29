using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FaceRecognition.Core.Models;
using FaceRecognition.Core.Services;

namespace FaceRecognition.Demo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=================================================================");
        Console.WriteLine("   .NET 8 原生人脸识别与身份核验系统 (OpenCvSharp5 + ONNX)");
        Console.WriteLine("=================================================================\n");

        // 1. 自动定位模型文件与底库
        string? yunetPath = FindCandidatePath(new[]
        {
            @"temp\face_detection_yunet\face_detection_yunet_2023mar.onnx",
            @"..\temp\face_detection_yunet\face_detection_yunet_2023mar.onnx",
            @"..\..\temp\face_detection_yunet\face_detection_yunet_2023mar.onnx",
            @"face_detection_yunet_2023mar.onnx"
        });

        string? sfacePath = FindCandidatePath(new[]
        {
            @"temp\face_recognition_sface\face_recognition_sface_2021dec.onnx",
            @"..\temp\face_recognition_sface\face_recognition_sface_2021dec.onnx",
            @"..\..\temp\face_recognition_sface\face_recognition_sface_2021dec.onnx",
            @"face_recognition_sface_2021dec.onnx"
        });

        string? jsonPath = FindCandidatePath(new[]
        {
            @"temp\sface_encodings.json",
            @"..\temp\sface_encodings.json",
            @"..\..\temp\sface_encodings.json",
            @"sface_encodings.json"
        });

        if (yunetPath == null || sfacePath == null || jsonPath == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[错误] 缺少必要资源文件！");
            Console.WriteLine($"  YuNet: {(yunetPath ?? "未找到")}");
            Console.WriteLine($"  SFace: {(sfacePath ?? "未找到")}");
            Console.WriteLine($"  底库JSON: {(jsonPath ?? "未找到，请先运行 python export_db_to_json.py")}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[检测模型] {yunetPath}");
        Console.WriteLine($"[识别模型] {sfacePath}");
        Console.WriteLine($"[特征底库] {jsonPath}\n");
        Console.ResetColor();

        using var faceService = new NativeFaceRecognitionService();
        faceService.Initialize(yunetPath, sfacePath, jsonPath);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✅ 服务初始化就绪 | 目标用户: [{faceService.UserName}] | 已加载样本: {faceService.EnrolledSampleCount} 个\n");
        Console.ResetColor();

        // 2. 根据命令行参数分流执行
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (mode == "image" && args.Length > 1)
        {
            float threshold = args.Length > 2 && float.TryParse(args[2], out var th) ? th : 0.38f;
            return RunImageVerification(faceService, args[1], threshold);
        }
        else if (mode == "monitor")
        {
            int duration = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 30;
            return await RunMonitorAsync(faceService, duration);
        }
        else if (mode == "check")
        {
            return RunHardwareCheck(faceService);
        }
        else if (mode == "camera" || string.IsNullOrEmpty(mode))
        {
            float timeout = args.Length > 1 && float.TryParse(args[1], out var to) ? to : 3.0f;
            float threshold = args.Length > 2 && float.TryParse(args[2], out var th) ? th : 0.38f;
            return await RunCameraVerificationAsync(faceService, timeout, threshold);
        }
        else
        {
            ShowUsage();
            return 0;
        }
    }

    private static async Task<int> RunCameraVerificationAsync(IFaceRecognitionService faceService, float timeout, float threshold)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"[模式] 调起本地 USB 摄像头进行本人核验");
        Console.WriteLine($"[规则] 允许超时时间: {timeout:F1} 秒 | 余弦相似度门限: >= {threshold:F3}");
        Console.WriteLine($"[策略] 任意一次单帧比对达标立即提前通过并放行");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(">>> 请正视摄像头，正在验证本人身份...\n");

        var result = await faceService.VerifyCameraAsync(
            timeoutSeconds: timeout,
            threshold: threshold,
            cameraId: 0);

        PrintResult(result);
        return result.Matched ? 0 : 1;
    }

    private static int RunImageVerification(IFaceRecognitionService faceService, string imagePath, float threshold)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"[模式] 静态图像单帧核验: {imagePath}");
        Console.WriteLine($"[门限] 余弦相似度: >= {threshold:F3}");
        Console.WriteLine("-----------------------------------------------------------------\n");

        var result = faceService.VerifyImage(imagePath, threshold);
        PrintResult(result);
        return result.Matched ? 0 : 1;
    }

    private static async Task<int> RunMonitorAsync(IFaceRecognitionService faceService, int durationSeconds)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"[模式] 摄像头持续监控统计: 持续 {durationSeconds} 秒，每秒检测 1 次");
        Console.WriteLine("-----------------------------------------------------------------\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[中断] 正在汇总监控统计数据...");
        };

        int total = 0;
        int matched = 0;
        int detected = 0;

        var start = DateTime.Now;
        while ((DateTime.Now - start).TotalSeconds < durationSeconds && !cts.IsCancellationRequested)
        {
            total++;
            var res = await faceService.VerifyCameraAsync(timeoutSeconds: 1.0f, threshold: 0.38f, cancellationToken: cts.Token);
            if (res.Status != FaceVerifyStatus.NoFace && res.Status != FaceVerifyStatus.NoCamera)
            {
                detected++;
            }
            if (res.Matched)
            {
                matched++;
            }

            double curRate = (double)matched / total * 100.0;
            string statusStr = res.Status switch
            {
                FaceVerifyStatus.Match => "[MATCH]   ",
                FaceVerifyStatus.Mismatch => "[MISMATCH]",
                FaceVerifyStatus.NoFace => "[NO_FACE] ",
                FaceVerifyStatus.NoCamera => "[NO_CAM]  ",
                _ => "[UNKNOWN] "
            };

            Console.WriteLine($"#{total:D3} | {statusStr} | 得分: {res.Confidence:F3} | 耗时: {res.ElapsedSeconds:F2}s | 匹配样本: {res.BestSample ?? "None"} | 累计识别率: {curRate:F1}% ({matched}/{total})");
            await Task.Delay(200);
        }

        Console.WriteLine("\n================ 监控统计总结 ================");
        Console.WriteLine($"总计采样: {total} 次");
        Console.WriteLine($"人脸检出: {detected} 次 (检出率: {((double)detected / (total > 0 ? total : 1) * 100.0):F1}%)");
        Console.WriteLine($"身份通过: {matched} 次 (总识别率: {((double)matched / (total > 0 ? total : 1) * 100.0):F1}%)");
        Console.WriteLine("==============================================");
        return 0;
    }

    private static int RunHardwareCheck(IFaceRecognitionService faceService)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine("[自检] 硬件与环境检查");
        Console.WriteLine("-----------------------------------------------------------------");
        bool camOk = faceService.CheckCameraAvailable(0);
        Console.WriteLine($"  1. 默认 USB 摄像头 (ID: 0): {(camOk ? "✅ 正常可用" : "❌ 无法打开或被占用")}");
        Console.WriteLine($"  2. 特征底库状态: ✅ 正常 (用户: {faceService.UserName}, 样本数: {faceService.EnrolledSampleCount})");
        Console.WriteLine($"  3. 模型推理引擎: ✅ OpenCvSharp5 原生 DNN 已加载");
        return camOk ? 0 : 1;
    }

    private static void PrintResult(FaceVerifyResult res)
    {
        Console.WriteLine("-------------------- 核验结果 --------------------");
        if (res.Matched)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✅ 状态: 【{res.Status}】 身份核验通过 (确认为操作人本人)");
            Console.ResetColor();
            Console.WriteLine($"👤 匹配用户: {res.UserName}");
            Console.WriteLine($"📈 综合相似度: {res.Confidence:F4} (门限: {res.Threshold:F3})");
            Console.WriteLine($"🎯 命中底库样本: {res.BestSample}");
            Console.WriteLine($"⏱️ 实际耗时: {res.ElapsedSeconds:F2} 秒 ({res.AttemptsCount} 帧)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠️ 状态: 【{res.Status}】 {res.Message}");
            Console.ResetColor();
            if (res.Confidence > 0)
            {
                Console.WriteLine($"📈 实测最高得分: {res.Confidence:F4} (门限: {res.Threshold:F3})");
            }
            Console.WriteLine($"⏱️ 实际耗时: {res.ElapsedSeconds:F2} 秒 ({res.AttemptsCount} 帧)");
        }
        Console.WriteLine("--------------------------------------------------\n");
    }

    private static string? FindCandidatePath(string[] candidates)
    {
        foreach (var p in candidates)
        {
            string abs = Path.GetFullPath(p);
            if (File.Exists(abs)) return abs;
        }
        return null;
    }

    private static void ShowUsage()
    {
        Console.WriteLine("使用方法:");
        Console.WriteLine("  dotnet run -- camera [timeoutSeconds=3.0] [threshold=0.38]   调起 USB 摄像头动态核验");
        Console.WriteLine("  dotnet run -- image <图片文件路径> [threshold=0.38]             核验单张本地图片");
        Console.WriteLine("  dotnet run -- monitor [durationSeconds=30]                    连续监控识别率");
        Console.WriteLine("  dotnet run -- check                                           硬件自检");
    }
}
