#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
局域网人脸识别 HTTP API 服务 (FastAPI + OpenCV YuNet + SFace + 可选 Anti-Spoofing 活体)
适用于本地与局域网调用，具备：
1. 摄像头生命周期自动管理与超时释放（避免 DirectShow 设备死锁）
2. 异步事件循环解耦（OpenCV 与 ONNX 推理全调度至后台线程，不阻塞 HTTP 响应）
3. 非侵入式极速健康自检（/health 接口不重开硬件）
4. 可选轻量静默活体防假检测 (可选参数 check_liveness)
5. 兼容 JSON 与 PKL 双格式特征底库
6. 支持文件上传 (Multipart) 与 Base64 JSON 双格式输入
"""

import os
import sys
import time
import json
import base64
import pickle
import socket
import asyncio
import threading
from typing import Optional, List, Tuple
from contextlib import asynccontextmanager

import cv2
import numpy as np
from fastapi import FastAPI, File, UploadFile, Query, Request, Body, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
import uvicorn

# 强制 UTF-8 标准输出
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

# ==================== 默认路径与候选检索 ====================
DEFAULT_DB_PATHS = [
    os.path.join("temp", "sface_encodings.json"),
    "sface_encodings.json",
    os.path.join("temp", "sface_encodings.pkl"),
    "sface_encodings.pkl",
]

DEFAULT_YUNET_PATHS = [
    os.path.join("temp", "face_detection_yunet", "face_detection_yunet_2023mar.onnx"),
    os.path.join("temp", "openvc_zoo_models", "face_detection_yunet", "face_detection_yunet_2023mar.onnx"),
    os.path.join("temp", "face-antispoof-onnx", "models", "detector_quantized.onnx"),
    os.path.join("temp", "face-antispoof-onnx", "models", "detector.onnx"),
    "face_detection_yunet_2023mar.onnx",
    "yunet.onnx",
]

DEFAULT_SFACE_PATHS = [
    os.path.join("temp", "face_recognition_sface", "face_recognition_sface_2021dec.onnx"),
    os.path.join("temp", "openvc_zoo_models", "face_recognition_sface", "face_recognition_sface_2021dec.onnx"),
    "face_recognition_sface_2021dec.onnx",
    "sface.onnx",
]

DEFAULT_LIVENESS_PATHS = [
    os.path.join("temp", "face-antispoof-onnx", "models", "best_model_quantized.onnx"),
    os.path.join("temp", "face-antispoof-onnx", "models", "best", "98.20", "best_model_quantized.onnx"),
    os.path.join("temp", "face-antispoof-onnx", "models", "best_model.onnx"),
]

HOST = "0.0.0.0"
PORT = 8999


def find_file(candidate_paths: List[str], desc: str = "文件") -> Optional[str]:
    for p in candidate_paths:
        abs_p = os.path.abspath(p)
        if os.path.exists(abs_p):
            return abs_p
    return None


def get_local_ip() -> str:
    """获取本机首选局域网 IPv4 地址"""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


# ==================== 活体检测算法工具函数 ====================
def crop_for_antispoof(img: np.ndarray, bbox_xywh: tuple, expansion_factor: float = 1.5) -> Optional[np.ndarray]:
    """根据人脸外接框以 expansion_factor 比例向外扩充，并使用反射边填充"""
    x, y, w, h = bbox_xywh
    if w <= 0 or h <= 0:
        return None
    max_dim = max(w, h)
    center_x = x + w / 2.0
    center_y = y + h / 2.0
    crop_size = int(max_dim * expansion_factor)
    if crop_size <= 0:
        return None

    crop_x = int(center_x - crop_size / 2.0)
    crop_y = int(center_y - crop_size / 2.0)

    H, W = img.shape[:2]
    crop_x1 = max(0, crop_x)
    crop_y1 = max(0, crop_y)
    crop_x2 = min(W, crop_x + crop_size)
    crop_y2 = min(H, crop_y + crop_size)

    top_pad = max(0, -crop_y)
    left_pad = max(0, -crop_x)
    bottom_pad = max(0, (crop_y + crop_size) - H)
    right_pad = max(0, (crop_x + crop_size) - W)

    if crop_x2 > crop_x1 and crop_y2 > crop_y1:
        cropped = img[crop_y1:crop_y2, crop_x1:crop_x2]
    else:
        cropped = np.zeros((0, 0, 3), dtype=img.dtype)

    return cv2.copyMakeBorder(cropped, top_pad, bottom_pad, left_pad, right_pad, cv2.BORDER_REFLECT_101)


def preprocess_antispoof(face_crop: np.ndarray, model_size: int = 128) -> np.ndarray:
    """保持纵横比 Letterbox 缩放，归一化到 [0, 1]，通道转为 CHW"""
    old_h, old_w = face_crop.shape[:2]
    ratio = float(model_size) / max(old_h, old_w)
    new_w, new_h = int(old_w * ratio), int(old_h * ratio)
    interpolation = cv2.INTER_LANCZOS4 if ratio > 1.0 else cv2.INTER_AREA
    resized = cv2.resize(face_crop, (new_w, new_h), interpolation=interpolation)

    dw = model_size - new_w
    dh = model_size - new_h
    top, bottom = dh // 2, dh - (dh // 2)
    left, right = dw // 2, dw - (dw // 2)

    padded = cv2.copyMakeBorder(resized, top, bottom, left, right, cv2.BORDER_REFLECT_101)
    chw = padded.transpose(2, 0, 1).astype(np.float32) / 255.0
    return np.expand_dims(chw, axis=0)


# ==================== 摄像头生命周期管理 ====================
class CameraManager:
    """
    负责本地 USB 摄像头的安全启闭、帧读取及空闲自动休眠。
    避免频繁启闭造成的硬件死锁，同时确保在无人请求时释放摄像头。
    """
    def __init__(self, idle_timeout: float = 15.0):
        self._cap: Optional[cv2.VideoCapture] = None
        self._current_camera_id: int = -1
        self._lock = threading.Lock()
        self._last_access_time: float = 0.0
        self._idle_timeout = idle_timeout
        self._active = False

    @property
    def is_active(self) -> bool:
        with self._lock:
            return self._cap is not None and self._cap.isOpened()

    def open_if_needed(self, camera_id: int = 0) -> bool:
        with self._lock:
            if self._cap is not None and self._cap.isOpened() and self._current_camera_id == camera_id:
                self._last_access_time = time.time()
                return True

            # 若已打开但编号不一致，先释放旧的
            if self._cap is not None:
                try:
                    self._cap.release()
                except Exception:
                    pass
                self._cap = None

            # 优先尝试 Windows DirectShow，失败回退默认
            cap = cv2.VideoCapture(camera_id, cv2.CAP_DSHOW)
            if not cap.isOpened():
                cap = cv2.VideoCapture(camera_id)

            if not cap.isOpened():
                self._cap = None
                self._active = False
                return False

            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

            # 快速预热抛弃 3 帧
            for _ in range(3):
                cap.read()

            self._cap = cap
            self._current_camera_id = camera_id
            self._last_access_time = time.time()
            self._active = True
            return True

    def read_frame(self, camera_id: int = 0) -> Tuple[bool, Optional[np.ndarray]]:
        with self._lock:
            if not self.open_if_needed(camera_id):
                return False, None
            ret, frame = self._cap.read()
            if ret:
                self._last_access_time = time.time()
            return ret, frame

    def release(self):
        with self._lock:
            if self._cap is not None:
                try:
                    self._cap.release()
                except Exception:
                    pass
                self._cap = None
            self._active = False

    def check_idle_timeout(self):
        """如果超过闲置时限，释放硬件"""
        with self._lock:
            if self._cap is not None and self._cap.isOpened():
                if time.time() - self._last_access_time > self._idle_timeout:
                    try:
                        self._cap.release()
                    except Exception:
                        pass
                    self._cap = None
                    self._active = False
                    print("[硬件监控] 摄像头闲置超时，已自动释放硬件句柄")


# ==================== 全局状态容器 ====================
class ServiceState:
    detector: Optional[cv2.FaceDetectorYN] = None
    recognizer: Optional[cv2.FaceRecognizerSF] = None
    liveness_session = None
    liveness_input_name: Optional[str] = None

    feature_matrix: Optional[np.ndarray] = None
    mean_feature: Optional[np.ndarray] = None
    sources: List[str] = []
    user_name: str = "Master"
    db_type: str = "none"

    camera_manager: CameraManager = CameraManager(idle_timeout=15.0)


state = ServiceState()


def enhance_illumination(bgr_img: np.ndarray) -> np.ndarray:
    """CLAHE 亮度通道自适应直方图均衡化"""
    lab = cv2.cvtColor(bgr_img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    cl = clahe.apply(l)
    return cv2.cvtColor(cv2.merge((cl, a, b)), cv2.COLOR_LAB2BGR)


def match_feature(live_feat_raw: np.ndarray) -> Tuple[float, Optional[str]]:
    """双轨比对：底库各样本最高相似度 + 均值中心向量比对"""
    live_norm = live_feat_raw.astype(np.float32)
    norm_val = np.linalg.norm(live_norm)
    if norm_val > 1e-6:
        live_norm = live_norm / norm_val

    # 1. 向量化计算与所有底库样本的余弦相似度
    sims = np.dot(state.feature_matrix, live_norm.T).flatten()
    best_idx = int(np.argmax(sims))
    max_sample_score = float(sims[best_idx])
    best_sample_name = state.sources[best_idx] if best_idx < len(state.sources) else None

    # 2. 与均值中心向量比对
    centroid_score = float(np.dot(state.mean_feature, live_norm.T)[0, 0])

    best_score = max(max_sample_score, centroid_score)
    return best_score, best_sample_name


def check_face_liveness(bgr_frame: np.ndarray, face_box_xywh: tuple, threshold: float = 0.5) -> Tuple[bool, float]:
    """
    执行轻量静默活体检测
    返回: (is_real, confidence_score)
    """
    if state.liveness_session is None or state.liveness_input_name is None:
        # 模型未加载时默认放行，置信度设为 1.0
        return True, 1.0

    try:
        # ONNX 模型使用 RGB 颜色空间
        rgb_frame = cv2.cvtColor(bgr_frame, cv2.COLOR_BGR2RGB)
        crop_face = crop_for_antispoof(rgb_frame, face_box_xywh, expansion_factor=1.5)
        if crop_face is None or crop_face.size == 0:
            return False, 0.0

        batch_tensor = preprocess_antispoof(crop_face, model_size=128)
        logits = state.liveness_session.run([], {state.liveness_input_name: batch_tensor})[0][0]

        real_logit, spoof_logit = float(logits[0]), float(logits[1])
        logit_diff = real_logit - spoof_logit

        # 将 logit_diff 通过 Sigmoid 映射到 [0, 1] 真实概率
        score = float(1.0 / (1.0 + np.exp(-logit_diff)))
        is_real = score >= threshold
        return is_real, round(score, 4)
    except Exception as e:
        print(f"[活体检测警告] 推理异常: {e}")
        return False, 0.0


# ==================== 生命周期管理 ====================
async def background_camera_monitor():
    """后台轻量协程：周期性检查摄像头是否空闲并释放"""
    while True:
        try:
            await asyncio.sleep(3.0)
            state.camera_manager.check_idle_timeout()
        except asyncio.CancelledError:
            break
        except Exception:
            pass


@asynccontextmanager
async def lifespan(app: FastAPI):
    # 1. 加载人脸检测模型 (YuNet)
    yunet_path = find_file(DEFAULT_YUNET_PATHS, "YuNet 检测模型")
    if not yunet_path:
        raise FileNotFoundError(f"未找到 YuNet 模型文件，已检索: {DEFAULT_YUNET_PATHS}")
    print(f"[模型就绪] YuNet 检测模型: {yunet_path}")
    state.detector = cv2.FaceDetectorYN.create(yunet_path, "", (640, 480), 0.75, 0.3)

    # 2. 加载人脸识别模型 (SFace)
    sface_path = find_file(DEFAULT_SFACE_PATHS, "SFace 识别模型")
    if not sface_path:
        raise FileNotFoundError(f"未找到 SFace 模型文件，已检索: {DEFAULT_SFACE_PATHS}")
    print(f"[模型就绪] SFace 识别模型: {sface_path}")
    state.recognizer = cv2.FaceRecognizerSF.create(sface_path, "")

    # 3. 加载静默活体模型 (可选)
    liveness_path = find_file(DEFAULT_LIVENESS_PATHS, "活体检测模型")
    if liveness_path:
        try:
            import onnxruntime as ort
            sess_options = ort.SessionOptions()
            sess_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
            state.liveness_session = ort.InferenceSession(liveness_path, sess_options=sess_options, providers=["CPUExecutionProvider"])
            state.liveness_input_name = state.liveness_session.get_inputs()[0].name
            print(f"[模型就绪] 活体检测模型 (Anti-Spoofing): {liveness_path}")
        except Exception as ex:
            print(f"[活体模型跳过] 无法初始化 ONNX Runtime 活体模型: {ex}")
    else:
        print("[活体模型跳过] 未检测到活体模型，相关可选功能将以直通模式运行")

    # 4. 加载特征底库 (优先 JSON，回退 PKL)
    db_path = find_file(DEFAULT_DB_PATHS, "人脸特征底库")
    if not db_path:
        raise FileNotFoundError(f"未找到特征底库文件，请先运行 build_face_db.py")

    print(f"[底库加载] 正在读取: {db_path}")
    if db_path.endswith(".json"):
        with open(db_path, "r", encoding="utf-8") as f:
            db = json.load(f)
        feat_data = db.get("features", db.get("feature_matrix"))
        state.feature_matrix = np.array(feat_data, dtype=np.float32)
        state.mean_feature = np.array(db["mean_feature"], dtype=np.float32).reshape(1, -1)
        state.sources = db.get("sources", [])
        state.user_name = db.get("user_name", "Master")
        state.db_type = "json"
    else:
        with open(db_path, "rb") as f:
            db = pickle.load(f)
        feat_data = db.get("feature_matrix", db.get("features"))
        state.feature_matrix = np.array(feat_data, dtype=np.float32)
        state.mean_feature = np.array(db["mean_feature"], dtype=np.float32).reshape(1, -1)
        state.sources = db.get("sources", [])
        state.user_name = db.get("user_name", "Master")
        state.db_type = "pickle"

    # 预先归一化特征底库以加速余弦相似度计算
    norms = np.linalg.norm(state.feature_matrix, axis=1, keepdims=True)
    norms[norms == 0] = 1.0
    state.feature_matrix = state.feature_matrix / norms

    mean_norm = np.linalg.norm(state.mean_feature)
    if mean_norm > 1e-6:
        state.mean_feature = state.mean_feature / mean_norm

    print(f"[服务就绪] 底库载入成功！用户: {state.user_name}，样本数: {len(state.sources)} ({state.db_type.upper()})")

    # 启动后台摄像头闲置监控任务
    monitor_task = asyncio.create_task(background_camera_monitor())

    yield

    # 退出时释放资源
    monitor_task.cancel()
    state.camera_manager.release()
    print("[服务关闭] 硬件句柄与推理资源已完全释放")


# ==================== FastAPI 应用 ====================
app = FastAPI(
    title="Face Verification API Service",
    description="本地/局域网人脸识别与活体核验微服务",
    version="2.0.0",
    lifespan=lifespan
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ==================== 请求与响应模型 ====================
class CameraVerifyRequest(BaseModel):
    timeout_seconds: float = Field(default=3.0, ge=0.5, le=30.0, description="最大核验等待超时秒数")
    threshold: float = Field(default=0.38, ge=0.1, le=0.9, description="判定通过的余弦相似度门限")
    camera_id: int = Field(default=0, description="摄像头设备编号")
    min_face_height: int = Field(default=80, description="过滤的最小人脸像素高度")
    check_liveness: bool = Field(default=False, description="是否启用静默活体防假检测 (可选)")
    liveness_threshold: float = Field(default=0.5, ge=0.0, le=1.0, description="活体判定置信度门限")


class ImageVerifyRequest(BaseModel):
    image_base64: str = Field(description="Base64 编码的图像数据 (支持带或不带 data:image/xxx 前缀)")
    threshold: float = Field(default=0.38, ge=0.1, le=0.9, description="判定通过的余弦相似度门限")
    check_liveness: bool = Field(default=False, description="是否启用静默活体防假检测 (可选)")
    liveness_threshold: float = Field(default=0.5, ge=0.0, le=1.0, description="活体判定置信度门限")


class VerifyResponse(BaseModel):
    status: str = Field(description="状态码: MATCH | MISMATCH | NO_FACE | NO_CAMERA | SPOOF_DETECTED | INVALID_IMAGE")
    matched: bool = Field(description="是否确认是本人放行")
    user_name: str = Field(description="目标用户名")
    confidence: float = Field(description="实测最高综合余弦相似度")
    threshold: float = Field(description="使用的比对阈值")
    best_sample: Optional[str] = Field(default=None, description="命中的底库照片文件名")
    liveness_checked: bool = Field(description="本次是否执行了活体检测")
    liveness_passed: Optional[bool] = Field(default=None, description="活体检测是否通过")
    liveness_score: Optional[float] = Field(default=None, description="活体检测实测置信度")
    elapsed_seconds: float = Field(description="本次核验总耗时(秒)")
    attempts_count: int = Field(description="采样帧数")
    message: str = Field(description="中文业务说明")


# ==================== 接口实现 ====================
@app.get("/api/v1/health")
async def health_check():
    """
    非侵入式健康探测：返回服务状态与底库信息，绝不重复启闭摄像头硬件
    """
    return {
        "status": "OK",
        "service": "face_verification_api",
        "user_name": state.user_name,
        "db_samples_count": len(state.sources),
        "db_type": state.db_type,
        "camera_active": state.camera_manager.is_active,
        "liveness_model_ready": state.liveness_session is not None,
        "server_time": time.strftime("%Y-%m-%d %H:%M:%S")
    }


def _process_single_frame(
    frame: np.ndarray,
    min_face_height: int,
    check_liveness: bool,
    liveness_threshold: float,
    match_threshold: float
) -> Tuple[Optional[str], float, Optional[str], Optional[bool], Optional[float]]:
    """
    单帧同步处理函数（调度在后台工作线程中执行）：
    检测 -> 尺寸过滤 -> CLAHE -> 活体检查(可选) -> 特征对齐与提取 -> 向量比对
    """
    enhanced = enhance_illumination(frame)
    h, w, _ = frame.shape
    state.detector.setInputSize((w, h))
    _, faces = state.detector.detect(enhanced)

    if faces is None or len(faces) == 0:
        return "NO_FACE", 0.0, None, None, None

    # 取面积最大的人脸
    face = max(faces, key=lambda f: f[2] * f[3])
    box_w = int(face[2])
    box_h = int(face[3])
    if box_h < min_face_height:
        return "NO_FACE", 0.0, None, None, None

    x = int(face[0])
    y = int(face[1])
    bbox_xywh = (x, y, box_w, box_h)

    # 1. 可选活体检测
    liveness_passed = None
    liveness_score = None
    if check_liveness:
        is_real, score = check_face_liveness(frame, bbox_xywh, threshold=liveness_threshold)
        liveness_passed = is_real
        liveness_score = score
        if not is_real:
            return "SPOOF_DETECTED", 0.0, None, liveness_passed, liveness_score

    # 2. 特征对齐与提取
    aligned = state.recognizer.alignCrop(enhanced, face)
    live_feat = state.recognizer.feature(aligned)
    sim_score, sample_name = match_feature(live_feat)

    status = "MATCH" if sim_score >= match_threshold else "MISMATCH"
    return status, sim_score, sample_name, liveness_passed, liveness_score


@app.post("/api/v1/verify/camera", response_model=VerifyResponse)
async def verify_by_camera(req: CameraVerifyRequest):
    """
    调起本机摄像头在窗口期内高频采样核验。
    采用后台线程执行，任意一帧匹配成功立即提前退出放行（毫秒级极速响应）。
    """
    start_time = time.time()

    # 先验证摄像头能否打开
    can_open = await asyncio.to_thread(state.camera_manager.open_if_needed, req.camera_id)
    if not can_open:
        return VerifyResponse(
            status="NO_CAMERA",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=req.threshold,
            best_sample=None,
            liveness_checked=req.check_liveness,
            liveness_passed=None,
            liveness_score=None,
            elapsed_seconds=round(time.time() - start_time, 2),
            attempts_count=0,
            message=f"无法打开本地摄像头设备 (ID: {req.camera_id})，请检查硬件连接或驱动状态"
        )

    attempts = 0
    highest_score = 0.0
    best_sample_name = None
    face_ever_detected = False
    spoof_detected_count = 0
    last_liveness_score = None

    while (time.time() - start_time) < req.timeout_seconds:
        # 在后台线程中抓取一帧
        ret, frame = await asyncio.to_thread(state.camera_manager.read_frame, req.camera_id)
        if not ret or frame is None:
            await asyncio.sleep(0.04)
            continue

        attempts += 1

        # 在后台线程中执行 DNN 检测与特征比对
        status, score, sample, live_pass, live_score = await asyncio.to_thread(
            _process_single_frame,
            frame,
            req.min_face_height,
            req.check_liveness,
            req.liveness_threshold,
            req.threshold
        )

        if live_score is not None:
            last_liveness_score = live_score

        if status == "SPOOF_DETECTED":
            spoof_detected_count += 1
            await asyncio.sleep(0.05)
            continue

        if status != "NO_FACE":
            face_ever_detected = True
            if score > highest_score:
                highest_score = score
                best_sample_name = sample

            # 任意一帧比对通过，立即放行！
            if status == "MATCH":
                cost = round(time.time() - start_time, 2)
                return VerifyResponse(
                    status="MATCH",
                    matched=True,
                    user_name=state.user_name,
                    confidence=round(score, 4),
                    threshold=req.threshold,
                    best_sample=sample,
                    liveness_checked=req.check_liveness,
                    liveness_passed=live_pass,
                    liveness_score=live_score,
                    elapsed_seconds=cost,
                    attempts_count=attempts,
                    message="身份核验通过"
                )

        # 间隔 40ms 进行下一次轮询采样，让出 CPU
        await asyncio.sleep(0.04)

    total_cost = round(time.time() - start_time, 2)

    # 超时仍未命中放行条件
    if not face_ever_detected and spoof_detected_count > 0:
        return VerifyResponse(
            status="SPOOF_DETECTED",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=req.threshold,
            best_sample=None,
            liveness_checked=True,
            liveness_passed=False,
            liveness_score=last_liveness_score,
            elapsed_seconds=total_cost,
            attempts_count=attempts,
            message="活体检测失败：检测到疑似照片翻拍或非真人面孔"
        )
    elif not face_ever_detected:
        return VerifyResponse(
            status="NO_FACE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=req.threshold,
            best_sample=None,
            liveness_checked=req.check_liveness,
            liveness_passed=None,
            liveness_score=None,
            elapsed_seconds=total_cost,
            attempts_count=attempts,
            message=f"在 {req.timeout_seconds:.1f} 秒内未检测到清晰人脸，请正对摄像头"
        )
    else:
        return VerifyResponse(
            status="MISMATCH",
            matched=False,
            user_name=state.user_name,
            confidence=round(highest_score, 4),
            threshold=req.threshold,
            best_sample=best_sample_name,
            liveness_checked=req.check_liveness,
            liveness_passed=True if req.check_liveness else None,
            liveness_score=last_liveness_score,
            elapsed_seconds=total_cost,
            attempts_count=attempts,
            message=f"人脸相似度不足 (最高 {highest_score:.3f} < 门限 {req.threshold:.3f})，非本人操作"
        )


@app.post("/api/v1/verify/image", response_model=VerifyResponse)
async def verify_by_image(
    file: Optional[UploadFile] = File(None),
    threshold: float = Query(default=0.38),
    check_liveness: bool = Query(default=False),
    liveness_threshold: float = Query(default=0.5),
    json_req: Optional[ImageVerifyRequest] = Body(None)
):
    """
    单张静态图片核验接口。
    同时支持 multipart/form-data 文件上传 与 application/json Base64 两种方式。
    """
    start_time = time.time()
    img_bytes = None

    if file is not None:
        img_bytes = await file.read()
    elif json_req is not None and json_req.image_base64:
        threshold = json_req.threshold
        check_liveness = json_req.check_liveness
        liveness_threshold = json_req.liveness_threshold
        b64_data = json_req.image_base64
        if "," in b64_data:
            b64_data = b64_data.split(",", 1)[1]
        try:
            img_bytes = base64.b64decode(b64_data)
        except Exception:
            img_bytes = None

    if not img_bytes:
        return VerifyResponse(
            status="INVALID_IMAGE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            liveness_checked=check_liveness,
            liveness_passed=None,
            liveness_score=None,
            elapsed_seconds=round(time.time() - start_time, 2),
            attempts_count=1,
            message="未提供有效的图像数据，请上传文件或提供 Base64"
        )

    # 解码图片
    nparr = np.frombuffer(img_bytes, np.uint8)
    img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

    if img is None:
        return VerifyResponse(
            status="INVALID_IMAGE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            liveness_checked=check_liveness,
            liveness_passed=None,
            liveness_score=None,
            elapsed_seconds=round(time.time() - start_time, 2),
            attempts_count=1,
            message="图像无法解码，请提供有效的 JPG 或 PNG 文件"
        )

    # 调度在后台线程执行
    status, score, sample, live_pass, live_score = await asyncio.to_thread(
        _process_single_frame,
        img,
        50,  # 图片模式允许较小人脸
        check_liveness,
        liveness_threshold,
        threshold
    )

    cost = round(time.time() - start_time, 2)

    if status == "NO_FACE":
        return VerifyResponse(
            status="NO_FACE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            liveness_checked=check_liveness,
            liveness_passed=None,
            liveness_score=None,
            elapsed_seconds=cost,
            attempts_count=1,
            message="图片中未检测到有效人脸"
        )
    elif status == "SPOOF_DETECTED":
        return VerifyResponse(
            status="SPOOF_DETECTED",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            liveness_checked=True,
            liveness_passed=False,
            liveness_score=live_score,
            elapsed_seconds=cost,
            attempts_count=1,
            message="活体检测失败：图片疑似照片翻拍或打印件"
        )
    else:
        is_match = (status == "MATCH")
        return VerifyResponse(
            status="MATCH" if is_match else "MISMATCH",
            matched=is_match,
            user_name=state.user_name,
            confidence=round(score, 4),
            threshold=threshold,
            best_sample=sample,
            liveness_checked=check_liveness,
            liveness_passed=live_pass,
            liveness_score=live_score,
            elapsed_seconds=cost,
            attempts_count=1,
            message="身份核验通过" if is_match else f"人脸不匹配 (相似度 {score:.3f} < 门限 {threshold:.3f})"
        )


def main():
    local_ip = get_local_ip()
    print("=" * 60)
    print(f"  人脸识别本地与局域网服务正在启动...")
    print(f"  本机回环访问: http://127.0.0.1:{PORT}")
    print(f"  局域网访问:   http://{local_ip}:{PORT}")
    print(f"  API 文档地址: http://{local_ip}:{PORT}/docs")
    print("=" * 60)
    uvicorn.run(app, host=HOST, port=PORT, log_level="info")


if __name__ == "__main__":
    main()
