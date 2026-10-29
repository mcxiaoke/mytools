# Windows 10 USB摄像头高可靠人脸识别实现方案（现代 ONNX 架构版）

## 1. 为什么抛弃 `dlib` 转向现代 `YuNet + SFace` 架构？

早期教程中常见的 `face_recognition`（基于 2017 年前后的 `dlib`）不仅在 Python 3.12+ 环境下存在严重的 C++ 编译与废弃包（`pkg_resources`）兼容问题，其算法底层也已落后两代。

本方案采用 **OpenCV 4.8+ 原生内置的现代 ONNX 推理管线（`YuNet` 人脸检测 + `SFace` 人脸识别）**，具备以下核心优势：

| 维度 | 传统方案 (`dlib` / `face_recognition`) | 现代方案 (`YuNet` + `SFace` ONNX) |
| :--- | :--- | :--- |
| **环境依赖** | 需编译 C++ 扩展、依赖旧版 `setuptools` | **零额外废包依赖**，仅需 `opencv-python` 与 `numpy` |
| **歪头与俯仰容错** | 仅做矩形框裁剪，歪头时特征偏移大 | **自带五官 5 关键点仿射变换（AlignCrop）**，自动把歪斜人脸“拧正”后再提特征 |
| **特征区分度** | 旧式 Triplet Loss，亚洲人脸易撞脸 | 现代 Margin-based Loss，类内紧凑、类间距离大，戴眼镜容错极高 |
| **CPU 推理速度** | 较慢（单帧约 $50\text{ ms}\sim 100\text{ ms}$） | 极快（模型总计不足 $40\text{ MB}$，普通 CPU 轻松跑满 $30\sim 60\text{ FPS}$） |

---

## 2. 硬件机位与底库采集黄金法则（基于实测优化）

### 2.1 摄像头物理机位法则
1. **高机位微俯拍（核心）**：将 USB 摄像头固定在**显示器顶部正中央**，镜头略微向下俯视（约 $10^\circ\sim 15^\circ$）。
2. **规避背景强光源**：微俯视角可以将背后窗户的强光移出画面上半区，让摄像头的自动曝光（AE）以人脸为中心测光，彻底消除面部发黑与镜头眩光（Lens Flare）。
3. **消除透视形变**：平视微俯视角下，额头、眉弓、眼窝、鼻梁比例最舒展，日常低头看键盘或靠椅背时几何形变最小。

### 2.2 科学构建“多状态特征库”（4~6 张黄金样本）
不要在同一姿势下连拍多张，建议采集 **4~6 张覆盖日常真实变化**的照片入库：
1. **主基准照（黄金底库）**：白天自然光/窗前环境 + 戴眼镜 + 正常平视屏幕（如你已拍好的 `WIN_20261029_16_43_08_Pro.jpg`）。
2. **姿态容错照**：微低头（模拟看键盘或看手机状态）、左右微侧脸（约 $15^\circ\sim 25^\circ$）各 1 张。
3. **穿戴变化照**：如果平时偶尔摘眼镜，补录 1 张不戴眼镜的正脸照。
4. **夜间光照照**：晚上关窗帘、开启房间吸顶灯或桌面台灯时补录 1 张。

---

## 3. 提高识别准确度与容错率的五大工程策略

### 策略一：五官 5 点仿射对齐（5-Point Affine Alignment）
`YuNet` 在框出人脸的同时，会精准定位**左眼、右眼、鼻尖、左嘴角、右嘴角** 5 个关键点。通过 `recognizer.alignCrop(img, face)`，算法会先将倾斜、微侧的人脸进行几何校正，统一映射为标准正脸尺寸后再送入 `SFace` 提取 128 维特征向量，从根源上解决歪头识别率下降的问题。

### 策略二：“最大相似度 + 均值特征向量”双轨匹配
假设底库中录入了 $N$ 张不同状态的样本，提取并归一化后的特征向量集合为 $\{\mathbf{v}_1, \mathbf{v}_2, \dots, \mathbf{v}_N\}$，同时计算你的**平均特征中心（Centroid）** $\bar{\mathbf{v}}$：

$$\bar{\mathbf{v}} = \frac{\sum_{i=1}^{N} \mathbf{v}_i}{\left\|\sum_{i=1}^{N} \mathbf{v}_i\right\|_2}$$

实时捕获人脸特征 $\mathbf{v}_{\text{live}}$ 时，余弦相似度（Cosine Similarity）计算公式为：

$$S(\mathbf{v}_{\text{live}}, \mathbf{v}_i) = \frac{\mathbf{v}_{\text{live}} \cdot \mathbf{v}_i}{\|\mathbf{v}_{\text{live}}\|_2 \|\mathbf{v}_i\|_2}$$

* **判定逻辑**：取单样本最高得分 $S_{\max} = \max_i S(\mathbf{v}_{\text{live}}, \mathbf{v}_i)$ 与中心得分 $S_{\text{mean}} = S(\mathbf{v}_{\text{live}}, \bar{\mathbf{v}})$ 的综合最优值。官方默认余弦相似度阈值为 $0.363$（得分越高代表越相似，满分 $1.0$）。

### 策略三：CLAHE 逆光与暗光自适应均衡化
针对窗前逆光或夜间仅靠显示器补光的场景，在图像进入检测器前，将其转换至 LAB 色彩空间，仅对亮度通道（L）执行 **限制对比度自适应直方图均衡化（CLAHE）**，在不破坏肤色特征的前提下提亮暗部细节并抑制背景过曝。

### 策略四：时间维度的“滑动窗口投票机制”（防闪烁误触）
绝不在单帧识别通过时立即触发动作。维护一个长度为 $W=5$ 的滑动窗口队列，记录最近 5 次识别结果，只有当队列中命中本人的次数 $\ge 4$ 次时才执行目标动作，并进入冷却期（Cooldown）。

### 策略五：人脸尺寸与检测置信度双重过滤
过滤画面远处路过的小人脸（如高度小于 $80\text{ px}$ 的检测框）以及 `YuNet` 置信度低于 $0.85$ 的模糊帧，确保只有坐在电脑前清晰稳定的人脸才参与比对。

---

## 4. 极简环境配置与目录结构

### 4.1 安装依赖（Python 3.10 ~ 3.12 通用）
如果之前装过旧版包，可先清理，仅保留纯净的 `opencv-python` 和 `numpy`：

```powershell
pip uninstall face_recognition face_recognition_models dlib-bin -y
pip install --upgrade opencv-python numpy
```

### 4.2 推荐项目目录结构
将你已经下载好的模型文件与照片放在同一工作目录下（代码已做智能文件名兼容，无论是简写文件名还是官方原名均可自动识别）：

```text
face_project/
├── yunet.onnx                                 # 或官方原名 face_detection_yunet_2023mar.onnx
├── sface.onnx                                 # 或官方原名 face_recognition_sface_2021dec.onnx
├── faces_db/                                  # 存放你拍好的底库照片文件夹
│   └── WIN_20261029_16_43_08_Pro.jpg          # 你刚才拍好的黄金底库照片（可直接放进去）
├── 1_build_face_db.py                         # 底库构建与交互式补录脚本
├── 2_run_guard.py                             # 高容错实时识别与动作触发脚本
└── sface_encodings.pkl                        # 运行脚本1后自动生成的特征库文件
```

---

## 5. 完整可运行代码方案

### 第一步：底库构建与交互式补录脚本 (`1_build_face_db.py`)

此脚本支持**两种模式无缝结合**：
1. **自动扫描现有照片**：自动读取 `faces_db/` 文件夹（或当前目录）下的 `.jpg/.png` 照片提取特征。
2. **摄像头交互式补录**：随后可选打开摄像头，按 `空格键` 补充拍摄微低头、摘眼镜等姿态，一键融合生成特征库 `sface_encodings.pkl`。

```python
import os
import glob
import pickle
import cv2
import numpy as np

# ================= 配置区域 =================
USER_NAME = "Master"
DB_DIR = "faces_db"
OUTPUT_PKL = "sface_encodings.pkl"

# 自动兼容不同的模型文件名
YUNET_CANDIDATES = ["yunet.onnx", "face_detection_yunet_2023mar.onnx"]
SFACE_CANDIDATES = ["sface.onnx", "face_recognition_sface_2021dec.onnx"]
# ============================================

def find_model_file(candidates):
    for path in candidates:
        if os.path.exists(path):
            return path
    raise FileNotFoundError(f"未找到模型文件，请确保以下任意文件在当前目录: {candidates}")

def enhance_illumination(bgr_img):
    """CLAHE 亮度均衡化，改善逆光与暗光表现"""
    lab = cv2.cvtColor(bgr_img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    cl = clahe.apply(l)
    return cv2.cvtColor(cv2.merge((cl, a, b)), cv2.COLOR_LAB2BGR)

def extract_sface_feature(img, detector, recognizer):
    """检测最大人脸 -> 5点仿射对齐摆正 -> 提取128维特征向量"""
    enhanced = enhance_illumination(img)
    h, w, _ = enhanced.shape
    detector.setInputSize((w, h))
    _, faces = detector.detect(enhanced)
    if faces is None or len(faces) == 0:
        return None, None
    
    # 按面积选取画面中最大的一张脸
    best_face = max(faces, key=lambda f: f[2] * f[3])
    # 核心步骤：利用五官关键点将歪斜人脸仿射对齐为标准正脸
    aligned_face = recognizer.alignCrop(enhanced, best_face)
    feature = recognizer.feature(aligned_face)
    return feature, best_face

def main():
    yunet_path = find_model_file(YUNET_CANDIDATES)
    sface_path = find_model_file(SFACE_CANDIDATES)
    print(f"[初始化] 加载检测模型: {yunet_path} | 识别模型: {sface_path}")

    detector = cv2.FaceDetectorYN.create(yunet_path, "", (640, 480), 0.85, 0.3)
    recognizer = cv2.FaceRecognizerSF.create(sface_path, "")

    os.makedirs(DB_DIR, exist_ok=True)
    features = []
    sources = []

    # 1. 自动扫描 faces_db 目录及当前目录下的已有照片
    img_paths = glob.glob(os.path.join(DB_DIR, "*.jpg")) + \
                glob.glob(os.path.join(DB_DIR, "*.png")) + \
                glob.glob("WIN_*.jpg")
    # 去重
    img_paths = list(dict.fromkeys(img_paths))

    if img_paths:
        print(f"\n--- 正在从已有的 {len(img_paths)} 张照片提取特征 ---")
        for p in img_paths:
            img = cv2.imread(p)
            if img is None:
                continue
            feat, _ = extract_sface_feature(img, detector, recognizer)
            if feat is not None:
                features.append(feat)
                sources.append(os.path.basename(p))
                print(f"  [成功] 已提取样本: {os.path.basename(p)}")
            else:
                print(f"  [跳过] 未在图片中检测到清晰人脸: {os.path.basename(p)}")

    # 2. 询问是否开启摄像头补录更多姿态
    print(f"\n当前已收集 {len(features)} 个有效人脸特征。")
    choice = input("是否打开摄像头继续补录其他姿态（如微低头、摘眼镜、侧脸）？(y/n, 默认 y): ").strip().lower()
    
    if choice in ["", "y", "yes"] or len(features) == 0:
        cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
        print("\n=== 摄像头交互补录模式 ===")
        print("操作指南：摆好姿势后按 [空格键] 抓拍入库，按 [Q] 键结束并保存。")
        
        while True:
            ret, frame = cap.read()
            if not ret:
                print("无法读取摄像头！")
                break

            display = frame.copy()
            feat, face_box = extract_sface_feature(frame, detector, recognizer)

            if face_box is not None:
                x, y, w, h = list(map(int, face_box[:4]))
                cv2.rectangle(display, (x, y), (x + w, y + h), (0, 255, 0), 2)
                # 绘制五官 5 个关键点（双眼、鼻尖、嘴角）
                landmarks = face_box[4:14].reshape((5, 2)).astype(int)
                for (lx, ly) in landmarks:
                    cv2.circle(display, (lx, ly), 3, (0, 255, 255), -1)

            cv2.putText(display, f"Enrolled Samples: {len(features)} | [SPACE] Capture | [Q] Save & Quit",
                        (15, 35), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (0, 255, 255), 2)
            cv2.imshow("Enroll Face (YuNet + SFace)", display)

            key = cv2.waitKey(1) & 0xFF
            if key == ord(' '):
                if feat is not None:
                    features.append(feat)
                    snap_name = f"live_capture_{len(features)}.jpg"
                    cv2.imwrite(os.path.join(DB_DIR, snap_name), frame)
                    sources.append(snap_name)
                    print(f"  [抓拍成功] 已保存第 {len(features)} 个特征样本 ({snap_name})")
                else:
                    print("  [警告] 当前画面未检测到清晰人脸，请调整姿势重试！")
            elif key == ord('q'):
                break

        cap.release()
        cv2.destroyAllWindows()

    if not features:
        print("[错误] 没有任何有效特征入库，程序退出。")
        return

    # 3. 计算归一化均值特征向量（Centroid），用于提升综合容错率
    feat_matrix = np.vstack(features)
    mean_feat = np.mean(feat_matrix, axis=0, keepdims=True)
    mean_feat = (mean_feat / np.linalg.norm(mean_feat)).astype(np.float32)

    db_data = {
        "user_name": USER_NAME,
        "features": features,       # 各个独立姿态的特征列表
        "mean_feature": mean_feat,  # 融合中心特征
        "sources": sources
    }

    with open(OUTPUT_PKL, "wb") as f:
        pickle.dump(db_data, f)

    print(f"\n✅ 特征库构建完毕！共包含 {len(features)} 个样本，已保存至 {OUTPUT_PKL}")

if __name__ == "__main__":
    main()
```

---

### 第二步：高容错实时识别与任务触发脚本 (`2_run_guard.py`)

此脚本加载 `sface_encodings.pkl`，实时运行光照补偿、五官对齐、多样本双轨比对与滑动窗口防抖投票。

```python
import os
import time
import pickle
from collections import deque
import cv2
import numpy as np

# ================= 核心调优参数 =================
DB_PKL = "sface_encodings.pkl"
YUNET_CANDIDATES = ["yunet.onnx", "face_detection_yunet_2023mar.onnx"]
SFACE_CANDIDATES = ["sface.onnx", "face_recognition_sface_2021dec.onnx"]

COSINE_THRESHOLD = 0.38   # 余弦相似度阈值：官方基准为 0.363。建议在 0.36(高容错) ~ 0.45(高安全) 之间微调
MIN_FACE_HEIGHT = 80      # 最小人脸高度像素（过滤远处背景人像）
DETECT_EVERY_N_FRAMES = 2 # 每 2 帧执行一次推理，大幅降低后台 CPU 占用
VOTE_WINDOW_SIZE = 5      # 滑动窗口长度（记录最近 5 次检测结果）
VOTE_PASS_COUNT = 4       # 5 次中至少 4 次匹配成功才触发任务
COOLDOWN_SECONDS = 10     # 触发动作后的冷却时间（秒）
# ================================================

def find_model_file(candidates):
    for path in candidates:
        if os.path.exists(path):
            return path
    raise FileNotFoundError(f"未找到模型文件: {candidates}")

def enhance_illumination(bgr_img):
    """CLAHE 自适应光照均衡"""
    lab = cv2.cvtColor(bgr_img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    cl = clahe.apply(l)
    return cv2.cvtColor(cv2.merge((cl, a, b)), cv2.COLOR_LAB2BGR)

def trigger_custom_action(user_name, score):
    """识别稳定通过后执行的自定义自动化操作"""
    timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
    print(f"\n🚀 [{timestamp}] 身份确认: {user_name} (综合相似度: {score:.3f}) -> 正在执行预设动作...")
    # ---------------------------------------------------------
    # 在下方编写你的自定义逻辑（例如：调用系统命令、解锁、发通知、打开程序等）
    # import subprocess
    # subprocess.Popen(["notepad.exe"])
    # ---------------------------------------------------------

def main():
    if not os.path.exists(DB_PKL):
        raise FileNotFoundError(f"未找到特征库 {DB_PKL}，请先运行 1_build_face_db.py！")

    with open(DB_PKL, "rb") as f:
        db = pickle.load(f)

    user_name = db["user_name"]
    known_features = db["features"]
    mean_feature = db["mean_feature"]
    print(f"[就绪] 已加载用户 [{user_name}] 的 {len(known_features)} 个姿态样本：{db['sources']}")

    yunet_path = find_model_file(YUNET_CANDIDATES)
    sface_path = find_model_file(SFACE_CANDIDATES)
    detector = cv2.FaceDetectorYN.create(yunet_path, "", (640, 480), 0.85, 0.3)
    recognizer = cv2.FaceRecognizerSF.create(sface_path, "")

    cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
    vote_queue = deque(maxlen=VOTE_WINDOW_SIZE)
    last_trigger_time = 0
    frame_idx = 0

    # 缓存上一帧的绘制状态，保证跳帧检测时画面依然丝滑
    last_box = None
    last_score = 0.0
    last_matched = False

    print("=== 开始实时监控（按 Q 键退出） ===")
    while True:
        ret, frame = cap.read()
        if not ret:
            break

        frame_idx += 1
        if frame_idx % DETECT_EVERY_N_FRAMES == 0:
            enhanced = enhance_illumination(frame)
            h, w, _ = enhanced.shape
            detector.setInputSize((w, h))
            _, faces = detector.detect(enhanced)

            matched_this_frame = False
            best_score = 0.0
            current_box = None

            if faces is not None and len(faces) > 0:
                # 选取最大的人脸
                face = max(faces, key=lambda f: f[2] * f[3])
                box_x, box_y, box_w, box_h = list(map(int, face[:4]))

                if box_h >= MIN_FACE_HEIGHT:
                    current_box = (box_x, box_y, box_w, box_h)
                    aligned = recognizer.alignCrop(enhanced, face)
                    live_feat = recognizer.feature(aligned)

                    # 1. 与底库中每个姿态样本分别计算余弦相似度，取最高分
                    scores = [
                        recognizer.match(live_feat, ref_f, cv2.FaceRecognizerSF_FR_COSINE)
                        for ref_f in known_features
                    ]
                    max_sample_score = max(scores)

                    # 2. 与融合中心特征计算余弦相似度
                    centroid_score = recognizer.match(live_feat, mean_feature, cv2.FaceRecognizerSF_FR_COSINE)

                    # 综合评分：兼顾特定姿态命中与整体均值稳定性
                    best_score = max(max_sample_score, centroid_score)
                    matched_this_frame = best_score >= COSINE_THRESHOLD

            # 更新缓存与滑动投票队列
            last_box = current_box
            last_score = best_score
            last_matched = matched_this_frame
            vote_queue.append(1 if matched_this_frame else 0)

            # 检查滑动窗口是否达到触发条件
            if len(vote_queue) == VOTE_WINDOW_SIZE and sum(vote_queue) >= VOTE_PASS_COUNT:
                now = time.time()
                if now - last_trigger_time >= COOLDOWN_SECONDS:
                    trigger_custom_action(user_name, best_score)
                    last_trigger_time = now
                    vote_queue.clear()

        # 绘制实时 UI 反馈
        if last_box is not None:
            x, y, bw, bh = last_box
            color = (0, 255, 0) if last_matched else (0, 0, 255)
            tag = f"{user_name if last_matched else 'Unknown'} | Cosine: {last_score:.3f}"
            cv2.rectangle(frame, (x, y), (x + bw, y + bh), color, 2)
            cv2.putText(frame, tag, (x, max(25, y - 10)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

        # 显示投票进度与冷却状态
        cooldown_left = max(0, int(COOLDOWN_SECONDS - (time.time() - last_trigger_time)))
        status_text = f"Votes: {sum(vote_queue)}/{VOTE_WINDOW_SIZE} | Cooldown: {cooldown_left}s"
        cv2.putText(frame, status_text, (15, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.65, (255, 255, 0), 2)

        cv2.imshow("Modern Face Guard (YuNet + SFace)", frame)
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break

    cap.release()
    cv2.destroyAllWindows()

if __name__ == "__main__":
    main()
```

---

## 6. 参数调优与安全进阶指南

### 6.1 如何精准微调 `COSINE_THRESHOLD`？
注意：`SFace` 使用的**余弦相似度（Cosine Similarity）**与旧版 `dlib` 的欧氏距离正好相反——**数值越大代表越像**！

| 观察场景 | 典型 `Cosine` 得分范围 | 阈值 (`COSINE_THRESHOLD`) 调优建议 |
| :--- | :--- | :--- |
| **本人正对屏幕 / 微低头** | $0.48 \sim 0.75$ | 日常极佳状态，远高于默认阈值 |
| **本人大角度侧脸 / 昏暗光线** | $0.36 \sim 0.45$ | 如果希望极端角度也能秒认，阈值保持在 **$0.36 \sim 0.38$** |
| **外人（不同人）坐在镜头前** | $-0.10 \sim 0.25$ | 如果用于解锁等高安全场景，可将阈值上调至 **$0.42 \sim 0.45$** |

### 6.2 困难样本增量补录法
如果发现某个特定时刻（例如深夜只开显示器、或者戴了帽子）画面上的实时 `Cosine` 得分在 $0.34$ 左右徘徊：
* **不要盲目降低阈值**（低于 $0.35$ 会增加误认外人的风险）。
* 直接运行 `python 1_build_face_db.py`，在当前姿势下按一下空格键抓拍 1 张照片入库，该姿势下的得分会立刻跃升至 $0.60$ 以上。

### 6.3 进阶：如何防止别人拿你的手机照片骗过摄像头？
如果你用这个脚本做自动解锁或敏感权限控制，可以通过 `YuNet` 自带的 5 个关键点加入极简的**“转头/点头活体校验”**：
* 计算鼻尖坐标 $(x_{\text{nose}}, y_{\text{nose}})$ 与双眼连线中点的相对位移比例。
* 要求用户在 $2\text{ s}$ 内微微左右转头或点头一次，也可以直接集成开源的 `MiniFASNet`（静默活体检测 ONNX 模型，仅 $1.8\text{ MB}$）对裁剪出的人脸框做一次真假脸分类。