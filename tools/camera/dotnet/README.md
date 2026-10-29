# FaceRecognition .NET 8 原生人脸识别与身份核验方案

基于 **.NET 8** 与 **OpenCvSharp5 (OpenCV 5.0 原生 ONNX 推理引擎)** 实现的纯原生人脸身份认证库，实现零 Python 依赖、零网络通信的本地高速人脸检测、五官对齐与 128 维特征比对。

---

## 1. 解决方案结构

```text
dotnet/
├── FaceRecognition.slnx               # 现代 .NET 8/9 解决方案文件
├── README.md                          # 本说明文档
└── src/
    ├── FaceRecognition.Core/          # 核心类库 (跨平台 net8.0)
    │   ├── Models/
    │   │   ├── FaceVerifyStatus.cs    # 状态枚举 (Match, Mismatch, NoFace, NoCamera, InvalidImage)
    │   │   ├── FaceVerifyResult.cs    # 完整核验结果与置信度载荷
    │   │   └── FaceFeatureDatabase.cs # 多姿态特征底库与双轨余弦比对引擎
    │   └── Services/
    │       ├── IFaceRecognitionService.cs      # 抽象接口
    │       └── NativeFaceRecognitionService.cs # 原生 OpenCV 推理服务实现
    └── FaceRecognition.Demo/          # 控制台交互演示程序 (net8.0 Exe)
        └── Program.cs
```

---

## 2. 跨平台支持原理与说明

针对 **"OpenCvSharp 写着可以跨平台，我们的库如何跨平台？"** 的问题，核心工程模式如下：

### 2.1 架构解耦模式
- **`FaceRecognition.Core`（核心库）**：仅引用托管跨平台包 `OpenCvSharp5`。该项目为纯 C# 代码，**不与任何操作系统绑定**，编译生成的 `FaceRecognition.Core.dll` 可在任意平台上被复用。
- **`FaceRecognition.Demo`（宿主应用）**：按运行平台引入对应的 Native 原生底层运行时。

### 2.2 宿主项目的跨平台条件引用配置 (`.csproj`)
在最终发布运行的应用程序项目文件中，利用 MSBuild 的 `IsOSPlatform` 条件自动包含对应平台的原生 C++ 动态库：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <!-- 引用核心类库 -->
  <ItemGroup>
    <ProjectReference Include="..\FaceRecognition.Core\FaceRecognition.Core.csproj" />
  </ItemGroup>

  <!-- 1. Windows 平台 (自动输出 OpenCvSharpExtern.dll) -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <PackageReference Include="OpenCvSharp5.Windows" Version="5.0.0.20261018" />
  </ItemGroup>

  <!-- 2. Linux 平台 (Ubuntu/Debian 自动输出 libOpenCvSharpExtern.so) -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <PackageReference Include="OpenCvSharp5.Ubuntu" Version="5.0.0.20261018" />
  </ItemGroup>

  <!-- 3. macOS 平台 (自动输出 libOpenCvSharpExtern.dylib) -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <PackageReference Include="OpenCvSharp5.MacOS" Version="5.0.0.20261018" />
  </ItemGroup>

</Project>
```

---

## 3. 核心 API 使用指南

### 3.1 初始化服务
```csharp
using FaceRecognition.Core.Services;

// 创建服务单例 (实现 IDisposable)
using var faceService = new NativeFaceRecognitionService();

// 载入 YuNet 检测模型、SFace 识别模型及 JSON 格式多姿态特征库
faceService.Initialize(
    yunetPath: @"temp\face_detection_yunet\face_detection_yunet_2023mar.onnx",
    sfacePath: @"temp\face_recognition_sface\face_recognition_sface_2021dec.onnx",
    featureJsonPath: @"temp\sface_encodings.json"
);

Console.WriteLine($"用户: {faceService.UserName}, 已载入样本数: {faceService.EnrolledSampleCount}");
```

### 3.2 调起 USB 摄像头动态核验（推荐门禁/登录核验）
```csharp
// 允许 3.0 秒窗口期，余弦门限 >= 0.380
// 策略：任意一帧比对通过立即提前熔断返回（极速放行）
var result = await faceService.VerifyCameraAsync(
    timeoutSeconds: 3.0f,
    threshold: 0.38f,
    cameraId: 0
);

if (result.Matched)
{
    Console.WriteLine($"✅ 验证通过！相似度: {result.Confidence:F3}, 耗时: {result.ElapsedSeconds}s");
    // 放行后续关键业务...
}
else
{
    switch (result.Status)
    {
        case FaceVerifyStatus.Mismatch:
            Console.WriteLine($"⚠️ 非本人操作 (最高相似度 {result.Confidence:F3} < 0.380)");
            break;
        case FaceVerifyStatus.NoFace:
            Console.WriteLine("⚠️ 未检测到清晰人脸，请端正坐姿");
            break;
        case FaceVerifyStatus.NoCamera:
            Console.WriteLine("❌ 无法打开本地摄像头设备");
            break;
    }
}
```

### 3.3 静态图片核验
```csharp
// 支持本地文件路径、byte[] 二进制数据或 OpenCV Mat 对象
var result = faceService.VerifyImage(@"C:\path\to\photo.jpg", threshold: 0.38f);
Console.WriteLine(result.ToString());
```

---

## 4. Demo 控制台快速体验

可以直接在命令行通过参数运行演示程序：

```powershell
# 1. 调起 USB 摄像头进行实时动态核验 (默认 3 秒超时，0.38 门限)
dotnet run --project src\FaceRecognition.Demo -- camera 3.0 0.38

# 2. 静态图片核验
dotnet run --project src\FaceRecognition.Demo -- image "C:\Home\Temp\CameraRoll\WIN_20261029_17_07_44_Pro.jpg" 0.38

# 3. 摄像头持续监控 (持续 30 秒，每秒检测 1 次并汇总统计识别率)
dotnet run --project src\FaceRecognition.Demo -- monitor 30

# 4. 摄像头与模型健康检查
dotnet run --project src\FaceRecognition.Demo -- check
```

---

## 5. 算法与工程优化细节

1. **五官 5 点仿射校正 (AlignCrop)**：调用 `FaceRecognizerSF.AlignCrop`，利用双眼、鼻尖、嘴角将歪斜/俯仰的人脸自动对齐为 $112 \times 112$ 标准正脸后再提取 128 维向量。
2. **双轨余弦相似度匹配**：
   $$\text{Score} = \max\left(\max_{i} S(\mathbf{v}_{\text{live}}, \mathbf{v}_i), S(\mathbf{v}_{\text{live}}, \bar{\mathbf{v}})\right)$$
   同时比对多姿态所有注册样本与归一化中心向量（Centroid），兼顾特殊角度容错与标准姿态稳定性。
3. **CLAHE 逆光与暗部自适应**：在进入检测器前，将图像转入 LAB 色彩空间，仅对 L 通道执行限制对比度自适应直方图均衡化，消除背光与暗部发黑。
4. **内存管理规范**：严格使用 `using` 语句与 `Dispose()` 释放原生 OpenCV `cv::Mat` 非托管指针，避免长时间高频监控时的内存泄漏。
