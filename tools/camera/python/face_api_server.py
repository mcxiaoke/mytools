#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
人脸识别本地/边缘 HTTP API 服务 (FastAPI + OpenCV ONNX)
提供摄像头动态时间窗口核验与单图静态核验接口。
"""

import os
import sys
import time
import pickle
import asyncio
from typing import Optional
from contextlib import asynccontextmanager

import cv2
import numpy as np
from fastapi import FastAPI, File, UploadFile, Query
from pydantic import BaseModel, Field
import uvicorn

# 强制 UTF-8 输出
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

# ==================== 模型与特征库路径 ====================
DEFAULT_DB_PATHS = [
    os.path.join("temp", "sface_encodings.pkl"),
    "sface_encodings.pkl",
]

DEFAULT_YUNET_PATHS = [
    os.path.join("temp", "face_detection_yunet", "face_detection_yunet_2023mar.onnx"),
    os.path.join("temp", "openvc_zoo_models", "face_detection_yunet", "face_detection_yunet_2023mar.onnx"),
    os.path.join("temp", "openvc_zoo_models", "face_detection_yunet", "face_detection_yunet_2026may.onnx"),
    "face_detection_yunet_2023mar.onnx",
    "yunet.onnx",
]

DEFAULT_SFACE_PATHS = [
    os.path.join("temp", "face_recognition_sface", "face_recognition_sface_2021dec.onnx"),
    os.path.join("temp", "openvc_zoo_models", "face_recognition_sface", "face_recognition_sface_2021dec.onnx"),
    "face_recognition_sface_2021dec.onnx",
    "sface.onnx",
]

HOST = "127.0.0.1"
PORT = 8999


def find_file(candidate_paths, desc="文件"):
    for p in candidate_paths:
        abs_p = os.path.abspath(p)
        if os.path.exists(abs_p):
            return abs_p
    raise FileNotFoundError(f"未找到 {desc}，已检索候选路径: {candidate_paths}")


class GlobalState:
    detector: Optional[cv2.FaceDetectorYN] = None
    recognizer: Optional[cv2.FaceRecognizerSF] = None
    feature_matrix: Optional[np.ndarray] = None
    mean_feature: Optional[np.ndarray] = None
    sources: list = []
    user_name: str = "Master"
    camera_lock: asyncio.Lock = None


state = GlobalState()


def enhance_illumination(bgr_img: np.ndarray) -> np.ndarray:
    """CLAHE 亮度通道均衡化"""
    lab = cv2.cvtColor(bgr_img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    cl = clahe.apply(l)
    return cv2.cvtColor(cv2.merge((cl, a, b)), cv2.COLOR_LAB2BGR)


def match_feature(live_feat_raw: np.ndarray):
    """双轨比对：与底库所有样本取最大相似度 + 与均值特征比对"""
    live_norm = live_feat_raw.astype(np.float32)
    live_norm = live_norm / np.linalg.norm(live_norm)

    # 1. 向量化计算与所有底库样本的余弦相似度
    sims = np.dot(state.feature_matrix, live_norm.T).flatten()
    best_idx = int(np.argmax(sims))
    max_sample_score = float(sims[best_idx])
    best_sample_name = state.sources[best_idx]

    # 2. 与中心向量比对
    centroid_score = float(np.dot(state.mean_feature, live_norm.T)[0, 0])

    best_score = max(max_sample_score, centroid_score)
    return best_score, best_sample_name


@asynccontextmanager
async def lifespan(app: FastAPI):
    state.camera_lock = asyncio.Lock()
    yunet_path = find_file(DEFAULT_YUNET_PATHS, "YuNet 检测模型")
    sface_path = find_file(DEFAULT_SFACE_PATHS, "SFace 识别模型")
    db_path = find_file(DEFAULT_DB_PATHS, "特征库 PKL")

    print(f"[初始化] 正在加载 YuNet: {yunet_path}")
    print(f"[初始化] 正在加载 SFace: {sface_path}")
    state.detector = cv2.FaceDetectorYN.create(yunet_path, "", (640, 480), 0.75, 0.3)
    state.recognizer = cv2.FaceRecognizerSF.create(sface_path, "")

    with open(db_path, "rb") as f:
        db = pickle.load(f)
    state.feature_matrix = db["feature_matrix"]
    state.mean_feature = db["mean_feature"]
    state.sources = db["sources"]
    state.user_name = db.get("user_name", "Master")
    print(f"[就绪] 底库载入成功！用户: {state.user_name}，样本数: {len(state.sources)}")
    yield
    print("[关闭] 服务停止，释放资源。")


app = FastAPI(title="Face Verification API", version="1.0.0", lifespan=lifespan)


class CameraVerifyRequest(BaseModel):
    timeout_seconds: float = Field(default=3.0, ge=0.5, le=30.0, description="最大允许超时秒数")
    threshold: float = Field(default=0.38, ge=0.1, le=0.9, description="判定余弦相似度阈值")
    camera_id: int = Field(default=0, description="摄像头设备编号")
    strategy: str = Field(default="any_pass", description="验证策略: any_pass (任意一次成功即放行) | majority_vote")
    min_face_height: int = Field(default=80, description="过滤最小人脸高度")


class VerifyResponse(BaseModel):
    status: str = Field(description="状态码: MATCH | MISMATCH | NO_FACE | NO_CAMERA | INVALID_IMAGE")
    matched: bool = Field(description="是否确认是本人")
    user_name: str = Field(description="目标用户名")
    confidence: float = Field(description="实测最高综合余弦相似度")
    threshold: float = Field(description="所使用的阈值")
    best_sample: Optional[str] = Field(default=None, description="命中的底库照片文件名")
    elapsed_seconds: float = Field(description="本次核验总耗时(秒)")
    attempts_count: int = Field(description="采样帧数")
    message: str = Field(description="人读说明")


@app.get("/api/v1/health")
async def health_check():
    """硬件与服务就绪检查"""
    cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
    cam_ok = cap.isOpened()
    if cap.isOpened():
        cap.release()

    return {
        "status": "OK",
        "user_name": state.user_name,
        "db_samples_count": len(state.sources),
        "camera_available": cam_ok
    }


@app.post("/api/v1/verify/camera", response_model=VerifyResponse)
async def verify_by_camera(req: CameraVerifyRequest):
    """
    通过本机摄像头进行窗口期动态核验。
    只要在 timeout_seconds 内检测到人脸且相似度 >= threshold，立即提前退出并返回 MATCH。
    """
    start_time = time.time()

    async with state.camera_lock:
        cap = cv2.VideoCapture(req.camera_id, cv2.CAP_DSHOW)
        if not cap.isOpened():
            cap = cv2.VideoCapture(req.camera_id)
        if not cap.isOpened():
            return VerifyResponse(
                status="NO_CAMERA",
                matched=False,
                user_name=state.user_name,
                confidence=0.0,
                threshold=req.threshold,
                best_sample=None,
                elapsed_seconds=round(time.time() - start_time, 2),
                attempts_count=0,
                message="无法打开本地摄像头设备，请检查物理连接或驱动状态"
            )

        attempts = 0
        highest_score = 0.0
        best_matched_sample = None
        face_ever_detected = False

        # 快速预热 3 帧
        for _ in range(3):
            cap.read()

        try:
            while (time.time() - start_time) < req.timeout_seconds:
                ret, frame = cap.read()
                if not ret:
                    await asyncio.sleep(0.05)
                    continue

                attempts += 1
                enhanced = enhance_illumination(frame)
                h, w, _ = frame.shape
                state.detector.setInputSize((w, h))
                _, faces = state.detector.detect(enhanced)

                if faces is not None and len(faces) > 0:
                    face = max(faces, key=lambda f: f[2] * f[3])
                    box_h = int(face[3])
                    if box_h >= req.min_face_height:
                        face_ever_detected = True
                        aligned = state.recognizer.alignCrop(enhanced, face)
                        live_feat = state.recognizer.feature(aligned)

                        score, sample_name = match_feature(live_feat)
                        if score > highest_score:
                            highest_score = score
                            best_matched_sample = sample_name

                        if req.strategy == "any_pass" and score >= req.threshold:
                            cost = round(time.time() - start_time, 2)
                            return VerifyResponse(
                                status="MATCH",
                                matched=True,
                                user_name=state.user_name,
                                confidence=round(score, 4),
                                threshold=req.threshold,
                                best_sample=sample_name,
                                elapsed_seconds=cost,
                                attempts_count=attempts,
                                message="身份核验通过"
                            )

                await asyncio.sleep(0.1)

        finally:
            cap.release()

        total_cost = round(time.time() - start_time, 2)
        if not face_ever_detected:
            return VerifyResponse(
                status="NO_FACE",
                matched=False,
                user_name=state.user_name,
                confidence=0.0,
                threshold=req.threshold,
                best_sample=None,
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
                best_sample=best_matched_sample,
                elapsed_seconds=total_cost,
                attempts_count=attempts,
                message=f"人脸相似度不足 (最高 {highest_score:.3f} < 门限 {req.threshold:.3f})，非本人操作"
            )


@app.post("/api/v1/verify/image", response_model=VerifyResponse)
async def verify_by_image(file: UploadFile = File(...), threshold: float = Query(default=0.38)):
    """上传单帧图片进行静态人脸核验"""
    start_time = time.time()
    contents = await file.read()
    nparr = np.frombuffer(contents, np.uint8)
    img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

    if img is None:
        return VerifyResponse(
            status="INVALID_IMAGE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            elapsed_seconds=round(time.time() - start_time, 2),
            attempts_count=1,
            message="上传的图像数据无法解码，请提供有效的 JPG 或 PNG 文件"
        )

    enhanced = enhance_illumination(img)
    h, w, _ = img.shape
    state.detector.setInputSize((w, h))
    _, faces = state.detector.detect(enhanced)

    cost = round(time.time() - start_time, 2)
    if faces is None or len(faces) == 0:
        return VerifyResponse(
            status="NO_FACE",
            matched=False,
            user_name=state.user_name,
            confidence=0.0,
            threshold=threshold,
            best_sample=None,
            elapsed_seconds=cost,
            attempts_count=1,
            message="图片中未检测到有效人脸"
        )

    face = max(faces, key=lambda f: f[2] * f[3])
    aligned = state.recognizer.alignCrop(enhanced, face)
    live_feat = state.recognizer.feature(aligned)
    score, sample_name = match_feature(live_feat)

    is_match = score >= threshold
    return VerifyResponse(
        status="MATCH" if is_match else "MISMATCH",
        matched=is_match,
        user_name=state.user_name,
        confidence=round(score, 4),
        threshold=threshold,
        best_sample=sample_name,
        elapsed_seconds=cost,
        attempts_count=1,
        message="身份核验通过" if is_match else "图片人脸与底库不匹配"
    )


def main():
    uvicorn.run(app, host=HOST, port=PORT, log_level="info")


if __name__ == "__main__":
    main()
