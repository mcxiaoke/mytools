# USB 摄像头陌生人脸检测与桌面联动截取方案

> **文档状态**：已实机核对与验证通过  
> **适用环境**：Windows 10 / 11 64-bit  
> **技术栈**：OpenCV (YuNet + SFace) + Python (或 .NET 8 原生) + Windows GDI / mss

---

## 一、 方案背景与业务目标

本方案旨在利用连接在电脑上的常规 USB 摄像头，在电脑运行期间以指定时间间隔（如 2~5 秒）对镜头前人员进行人脸检测与身份核验：
1. **自动识别白名单人员**：核验摄像头前是否为预定义的目标人员（如机主本人）。
2. **侦测非预定义人员（陌生人）**：当检测到人脸且不属于白名单时，自动触发报警联动。
3. **拍照/录像与屏幕双重存证**：自动对陌生人进行间隔拍照（或短视频录制），并**同步截取当前电脑桌面操作画面**，带时间戳持久化存储至本地，实现完整的物理操作审计。

---

## 二、 硬件功耗、损耗与成本核算

### 1. 电量与功耗（极低）
- **USB 摄像头功耗**：常见的标准 USB 2.0 / 3.0 UVC 摄像头工作功耗在 **0.5W ~ 1.5W** 左右（5V、100mA ~ 300mA）。
- **电量核算**：即使摄像头持续 24 小时保持通电工作，整天耗电量不超过 **0.03 度**（度数 = $1.2\text{W} \times 24\text{h} / 1000 \approx 0.0288\text{ kWh}$）。
- **整机影响**：对于台式电脑，占整机功耗不足 1%；对于笔记本电脑，功耗相当于插了一个低功耗无线鼠标接收器，对整机续航影响极小。

### 2. 硬件磨损与使用寿命（几乎无损耗）
- **全固态无机械部件**：USB 摄像头采用 CMOS 图像传感器与**电子快门（Rolling/Global Electronic Shutter）**，内部没有任何机械反光板或机械快门叶片，不存在类似传统单反相机的“快门帘幕机械寿命限制”。
- **传感器寿命**：CMOS 传感器属于标准半导体器件，在正常室温与散热条件下，平均无故障运行时间（MTBF）通常在 50,000 小时以上（连续运行 5 年以上）。
- **发热表现**：长时间工作时摄像头外壳可能微温（约 35℃~42℃），属于光学元件与主控芯片的正常热量分布，完全处于安全工作温区内。

### 3. 计算与系统性能开销（轻量平稳）
- **推理耗时**：
  - 基于轻量级 **YuNet**（人脸检测）及 **SFace**（特征提取）现代 ONNX 模型，在普通 PC CPU 单核上：
    - YuNet 检测单次耗时约：**8 ~ 15 毫秒**；
    - SFace 特征提取单次耗时约：**10 ~ 20 毫秒**；
    - 屏幕截图（mss 或 GDI）耗时约：**10 ~ 25 毫秒**。
- **CPU 占用率**：如果每隔 3~5 秒进行一次核验，平摊后的 CPU 占用率通常**低于 1%**，对日常办公、代码编译或游戏娱乐几乎无感。
- **存储 I/O**：采用“**事件驱动存储**”机制。在无人在场或机主本人在场时，不产生磁盘写入；仅在确认陌生人触发告警时写入 JPEG 图片（每张 200~400 KB）和桌面截图（每张 500 KB~1.5 MB），对 SSD 寿命影响微乎其微。

### 4. 物理特性与隐私感知
- **工作状态指示灯（LED）**：大多数品牌 USB 摄像头在视频流处于开启（Streaming）状态时，硬件侧的指示灯（绿/蓝/白）会常亮。如需无感静默监控，需注意该物理指示灯的提示效应。

---

## 三、 系统架构与状态机设计

为确保系统运行稳定，不产生过频写入或误判漏判，系统设计为三层架构：

```
                ┌──────────────────────────────┐
                │   后台常驻视频流抓取线程     │ (清空环形缓冲，避免读到过期帧)
                └──────────────┬───────────────┘
                               │ 每隔 INTERVAL 秒取最新帧
                               ▼
                ┌──────────────────────────────┐
                │ CLAHE 亮度自适应 + YuNet 检测 │
                └──────────────┬───────────────┘
                               │
            ┌──────────────────┴──────────────────┐
            ▼                                     ▼
     【状态 A: 画面无人】                  【检测到人脸目标】
    - 重置陌生人计数器                            │
    - 维持静默待机                                ▼
                                     ┌────────────────────────┐
                                     │  SFace 提取 128D 特征  │
                                     │  双轨余弦相似度匹配    │
                                     └────────────┬───────────┘
                                                  │
                          ┌───────────────────────┴───────────────────────┐
                          ▼                                               ▼
                【状态 B: 确认为本人】                          【状态 C: 确认为陌生人】
             (余弦分值 >= 判定阈值)                         (余弦分值 < 判定阈值)
            - 允许正常操作                                  - 累加陌生人连续命中计数器
            - 清空陌生人计数与告警标志                      - 进入防抖判断窗口
                                                                          │
                                                                          ▼
                                                            ┌───────────────────────────┐
                                                            │ 满足防抖阈值 (连续 N 次)? │
                                                            └─────────────┬─────────────┘
                                                                          │ 是 (达到触发条件)
                                                                          ▼
                                                            ┌───────────────────────────┐
                                                            │ 执行告警与联动捕获：      │
                                                            │ 1. 抓取并保存摄像头图像   │
                                                            │ 2. 截取并保存电脑桌面画面 │
                                                            │ 3. 记录时间戳与审计日志   │
                                                            │ 4. 进入冷却期 (防止刷屏)  │
                                                            └───────────────────────────┘
```

### 关键控制机制：
1. **连续判定防抖（Debounce）**：
   - 避免因为机主侧脸、低头打哈欠、手托腮遮挡面部或背光时，偶发 1 帧匹配分数偏低而误报。
   - 策略：要求必须在时间窗口内**连续 2 ~ 3 次检测均判定为陌生人**，才真正激活捕获动作。
2. **告警冷却限制（Cooldown Throttle）**：
   - 当陌生人持续停留在电脑前时，避免每隔几秒连续触发写入造成磁盘塞满。
   - 策略：触发捕获后设定冷却周期（如 10 ~ 30 秒），冷却期内仅记录日志，限制重复保存。

---

## 四、 核心工程细节与解决方案

### 1. 避免 OpenCV 视频流缓存滞后（Buffer Lag）
- **现象与成因**：OpenCV 的 `cv2.VideoCapture` 底层驱动（DirectShow / MSMF）自带 1~5 帧的内部环形队列。如果主线程 `time.sleep(3)` 之后直接调用 `cap.read()`，读出来的画面常常是 **3 秒前的历史旧帧**。
- **解决方案**：
  - **方案 A（专用轻量读取线程）**：启动守护线程执行 `cap.read()`，将最新帧存入共享变量，主循环定时直接读取共享的最新帧（推荐）。
  - **方案 B（连续 grab 冲刷）**：在休眠循环中使用 `cap.grab()` 保持消耗流，检测时刻再执行 `cap.retrieve()`。

### 2. 摄像头保持常开 vs 频繁重新打开
- **禁止每次检测频繁 `VideoCapture(0)` 与 `release()`**：
  - USB 摄像头每次重新打开，硬件需要 0.5 ~ 1.5 秒进行 UVC 握手协议通信。
  - 启动初期数帧曝光和自动白平衡（AE/AWB）尚未收敛，极易抓出全黑或严重过曝的废片。
  - **规范做法**：程序启动时打开一次，并在退出时统一释放。

### 3. 屏幕桌面截图技术选型
- **方案 A：基于 Python `mss` 库（推荐）**
  - 特点：C 语言编写，跨平台，单次多屏合并全截耗时仅 10 ~ 20 毫秒，直接输出 numpy 数组，可与 OpenCV 完美对接。
- **方案 B：纯 Python 标准库 `ctypes` + Windows GDI API（零外部包依赖）**
  - 特点：直接调用 `user32.dll` 与 `gdi32.dll` 中的 `BitBlt` 复制屏幕位图，无需 `pip install` 任何截图库。

---

## 五、 完整实战参考实现 (Python)

以下提供一份完整且可独立运行的工程脚本骨架，整合了**后台实时帧缓冲**、**YuNet 人脸检测**、**SFace 特征比对**、**防抖机制**以及**摄像头与桌面同步存盘**：

```python
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
陌生人脸自动侦测与摄像头/桌面双重联动捕获守护程序
依赖: opencv-python, numpy, mss (可选或使用原生 ctypes)
"""

import os
import sys
import time
import pickle
import threading
from datetime import datetime
import cv2
import numpy as np

# 尝试导入 mss，若未安装则提供提示
try:
    import mss
    HAS_MSS = True
except ImportError:
    HAS_MSS = False

# ================= 核心参数配置 =================
INTERVAL_SEC = 2.5           # 人脸核验周期 (秒)
COSINE_THRESHOLD = 0.38      # 余弦相似度阈值 (高于此值判定为机主)
REQUIRED_STREAK = 2          # 防抖连续陌生人触发次数 (连续 2 次命中才抓拍)
COOLDOWN_SEC = 15.0          # 捕获触发后的冷却时间 (秒)
OUTPUT_DIR = "captured_events"

YUNET_PATH = "temp/face_detection_yunet/face_detection_yunet_2023mar.onnx"
SFACE_PATH = "temp/face_recognition_sface/face_recognition_sface_2021dec.onnx"
DB_PATH = "temp/sface_encodings.pkl"


class CameraReaderThread(threading.Thread):
    """专用后台读帧线程，彻底杜绝 OpenCV 缓冲区积压延迟"""
    def __init__(self, camera_id=0):
        super().__init__(daemon=True)
        self.cap = cv2.VideoCapture(camera_id, cv2.CAP_DSHOW)
        if not self.cap.isOpened():
            self.cap = cv2.VideoCapture(camera_id)
        if not self.cap.isOpened():
            raise RuntimeError(f"无法打开 USB 摄像头 (ID: {camera_id})")
        
        self.running = True
        self.latest_frame = None
        self.lock = threading.Lock()

    def run(self):
        while self.running:
            ret, frame = self.cap.read()
            if ret and frame is not None:
                with self.lock:
                    self.latest_frame = frame
            time.sleep(0.01)

    def get_latest_frame(self):
        with self.lock:
            return self.latest_frame.copy() if self.latest_frame is not None else None

    def stop(self):
        self.running = False
        time.sleep(0.1)
        self.cap.release()


def capture_desktop_screen(save_path: str):
    """截取全屏桌面并保存为图片"""
    if HAS_MSS:
        with mss.mss() as sct:
            # monitors[0] 包含所有显示器的完整合成跨屏
            monitor = sct.monitors[0]
            shot = np.array(sct.grab(monitor))
            # mss 抓取为 BGRA，转换后写入
            bgr_frame = cv2.cvtColor(shot, cv2.COLOR_BGRA2BGR)
            cv2.imwrite(save_path, bgr_frame)
    else:
        # 使用 Windows GDI ctypes 零依赖实现截屏
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.windll.user32
        gdi32 = ctypes.windll.gdi32

        w = user32.GetSystemMetrics(0)
        h = user32.GetSystemMetrics(1)
        hdc_screen = user32.GetDC(0)
        hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
        hbm = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
        gdi32.SelectObject(hdc_mem, hbm)
        gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, 0, 0, 0x00CC0020)  # SRCCOPY

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [
                ('biSize', wintypes.DWORD),
                ('biWidth', wintypes.LONG),
                ('biHeight', wintypes.LONG),
                ('biPlanes', wintypes.WORD),
                ('biBitCount', wintypes.WORD),
                ('biCompression', wintypes.DWORD),
                ('biSizeImage', wintypes.DWORD),
                ('biXPelsPerMeter', wintypes.LONG),
                ('biYPelsPerMeter', wintypes.LONG),
                ('biClrUsed', wintypes.DWORD),
                ('biClrImportant', wintypes.DWORD)
            ]

        bmi = BITMAPINFOHEADER()
        bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bmi.biWidth = w
        bmi.biHeight = -h  # top-down
        bmi.biPlanes = 1
        bmi.biBitCount = 24
        bmi.biCompression = 0

        buffer = ctypes.create_string_buffer(w * h * 3)
        gdi32.GetDIBits(hdc_mem, hbm, 0, h, buffer, ctypes.byref(bmi), 0)

        # 释放句柄
        gdi32.DeleteObject(hbm)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)

        img = np.frombuffer(buffer, dtype=np.uint8).reshape((h, w, 3))
        cv2.imwrite(save_path, img)


def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # 1. 加载人脸底库
    if not os.path.exists(DB_PATH):
        raise FileNotFoundError(f"未找到底库文件: {DB_PATH}")
    with open(DB_PATH, "rb") as f:
        db = pickle.load(f)
    feature_matrix = db["feature_matrix"]
    mean_feature = db["mean_feature"]

    # 2. 初始化 YuNet 与 SFace
    detector = cv2.FaceDetectorYN.create(YUNET_PATH, "", (640, 480), 0.75, 0.3)
    recognizer = cv2.FaceRecognizerSF.create(SFACE_PATH, "")

    # 3. 启动后台摄像头线程
    cam_reader = CameraReaderThread(camera_id=0)
    cam_reader.start()
    print("[初始化] 正在预热传感器...")
    time.sleep(1.5)

    print(f"[守护中] 监测已启动，采样周期: {INTERVAL_SEC}s, 阈值: {COSINE_THRESHOLD}")

    stranger_streak = 0
    last_trigger_time = 0.0

    try:
        while True:
            time.sleep(INTERVAL_SEC)
            frame = cam_reader.get_latest_frame()
            if frame is None:
                continue

            h, w, _ = frame.shape
            detector.setInputSize((w, h))

            # 执行人脸检测
            _, faces = detector.detect(frame)

            # 场景 A: 画面完全无人
            if faces is None or len(faces) == 0:
                stranger_streak = 0
                continue

            # 选取面积最大的人脸进行比对
            face = max(faces, key=lambda f: f[2] * f[3])
            aligned = recognizer.alignCrop(frame, face)
            live_feat = recognizer.feature(aligned).astype(np.float32)
            live_feat_norm = live_feat / np.linalg.norm(live_feat)

            # 计算相似度 (单样本最高分 与 中心样本分)
            sample_sims = np.dot(feature_matrix, live_feat_norm.T).flatten()
            max_sample_score = float(np.max(sample_sims))
            centroid_score = float(np.dot(mean_feature, live_feat_norm.T)[0, 0])
            best_score = max(max_sample_score, centroid_score)

            now = time.time()

            if best_score >= COSINE_THRESHOLD:
                # 场景 B: 确认为白名单本人
                stranger_streak = 0
            else:
                # 场景 C: 确认为非本人 (陌生人)
                stranger_streak += 1
                print(f"[警报提示] 侦测到非白名单人员，当前相似度: {best_score:.3f}，连续计数: {stranger_streak}")

                if stranger_streak >= REQUIRED_STREAK:
                    # 判断是否处于冷却期
                    if now - last_trigger_time >= COOLDOWN_SEC:
                        last_trigger_time = now
                        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
                        
                        cam_file = os.path.join(OUTPUT_DIR, f"{ts}_stranger_cam.jpg")
                        scr_file = os.path.join(OUTPUT_DIR, f"{ts}_desktop.png")

                        # 1. 抓存摄像头画面
                        cv2.imwrite(cam_file, frame)
                        # 2. 抓存当前桌面全屏画面
                        capture_desktop_screen(scr_file)

                        print(f"🚨 [证据已保存] 摄像头证据: {cam_file} | 桌面截图: {scr_file}")

    except KeyboardInterrupt:
        print("\n[退出] 接收到退出信号，正在释放资源...")
    finally:
        cam_reader.stop()


if __name__ == "__main__":
    main()
```

---

## 六、 Windows 特殊场景与系统限制

1. **Windows 锁屏状态（Win + L）与桌面渲染**：
   - 当 Windows 进入锁屏状态或睡眠唤醒后的登录界面时，操作系统安全机制会将桌面移入安全会话（Secure Desktop）。
   - 此时普通的 GDI / DirectX 桌面抓取只能截取到**系统锁屏壁纸或全黑画面**（这是 Windows 的操作系统级安全边界）。
   - **应对建议**：如果目标是防范“机主暂时离开但**电脑未锁屏**时被他人翻看”，本方案可 100% 完美记录操作界面与入侵者面部；若电脑已处于锁屏界面，重点记录摄像头入侵者画面即可。
2. **多显示器混合 DPI 缩放**：
   - 在双屏或多屏环境下，使用 `mss` 库的 `monitors[0]` 可以自动完成多屏坐标对齐并合成全景图，避免 Windows 系统 DPI 缩放造成的截屏错位或黑边。
3. **静默后台驻留**：
   - 脚本可通过 `pythonw.exe` 静默启动，不弹出控制台黑框；或打包为原生单文件 Windows 服务 / 托盘应用程序常驻运行。
