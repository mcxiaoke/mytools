#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
人脸特征库构建脚本 (基于 YuNet + SFace)
支持自动读取指定目录的所有素材照片，提取 128 维面部特征向量并计算多姿态融合中心特征。
"""

import os
import sys
import glob
import pickle
import argparse
import datetime
import cv2
import numpy as np

# 强制 UTF-8 输出以避免 Windows 默认 GBK 终端报错
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

# 模型默认搜索路径
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


def find_file(candidate_paths, desc="模型"):
    for p in candidate_paths:
        abs_p = os.path.abspath(p)
        if os.path.exists(abs_p):
            return abs_p
    raise FileNotFoundError(f"未找到 {desc} 文件，已检索候选路径: {candidate_paths}")


def enhance_illumination(bgr_img):
    """CLAHE 亮度通道自适应直方图均衡化，改善背光与暗部表现"""
    lab = cv2.cvtColor(bgr_img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    cl = clahe.apply(l)
    return cv2.cvtColor(cv2.merge((cl, a, b)), cv2.COLOR_LAB2BGR)


def extract_sface_feature(img, detector, recognizer, score_threshold=0.8):
    """检测最大人脸 -> 5点关键点仿射对齐 -> 提取 128 维特征向量"""
    enhanced = enhance_illumination(img)
    h, w, _ = enhanced.shape
    detector.setInputSize((w, h))
    detector.setScoreThreshold(score_threshold)
    _, faces = detector.detect(enhanced)
    if faces is None or len(faces) == 0:
        # 回退原图尝试一次
        detector.setInputSize((w, h))
        _, faces = detector.detect(img)
        if faces is None or len(faces) == 0:
            return None, None
        face = max(faces, key=lambda f: f[2] * f[3])
        aligned = recognizer.alignCrop(img, face)
        feat = recognizer.feature(aligned)
        return feat, face

    best_face = max(faces, key=lambda f: f[2] * f[3])
    aligned = recognizer.alignCrop(enhanced, best_face)
    feat = recognizer.feature(aligned)
    return feat, best_face


def build_face_database(source_dir, output_pkl, user_name="Master", yunet_path=None, sface_path=None):
    if not yunet_path:
        yunet_path = find_file(DEFAULT_YUNET_PATHS, "YuNet 检测模型")
    if not sface_path:
        sface_path = find_file(DEFAULT_SFACE_PATHS, "SFace 识别模型")

    print(f"[初始化] YuNet 模型: {yunet_path}")
    print(f"[初始化] SFace 模型: {sface_path}")
    print(f"[初始化] 照片源目录: {source_dir}")

    detector = cv2.FaceDetectorYN.create(yunet_path, "", (640, 480), 0.8, 0.3)
    recognizer = cv2.FaceRecognizerSF.create(sface_path, "")

    # 支持 jpg, jpeg, png 格式
    patterns = ["*.jpg", "*.jpeg", "*.png", "*.JPG", "*.JPEG", "*.PNG"]
    img_files = []
    for pat in patterns:
        img_files.extend(glob.glob(os.path.join(source_dir, pat)))
    img_files = sorted(list(dict.fromkeys(img_files)))

    if not img_files:
        raise ValueError(f"在目录 {source_dir} 未找到任何图片！")

    print(f"找到 {len(img_files)} 张图片素材，开始提取特征...")

    features = []
    sources = []
    details = []

    for idx, p in enumerate(img_files, start=1):
        filename = os.path.basename(p)
        img = cv2.imread(p)
        if img is None:
            print(f"  [{idx}/{len(img_files)}] 无法读取图片: {filename}")
            continue

        feat, face_box = extract_sface_feature(img, detector, recognizer)
        if feat is not None:
            # SFace 默认提取的特征已接近单位向量，确保其为 float32 且归一化
            feat = feat.astype(np.float32)
            feat_norm = feat / np.linalg.norm(feat)
            features.append(feat_norm)
            sources.append(filename)
            details.append({
                "file": filename,
                "box": [float(v) for v in face_box[:4]],
                "score": float(face_box[-1]),
                "status": "success"
            })
            print(f"  [{idx}/{len(img_files)}] 成功提取: {filename} (检测置信度: {face_box[-1]:.3f})")
        else:
            print(f"  [{idx}/{len(img_files)}] 未检测到清晰人脸: {filename}")
            details.append({
                "file": filename,
                "status": "no_face_detected"
            })

    if not features:
        raise RuntimeError("未能从任何照片中提取到有效人脸特征！")

    # 特征矩阵 (N, 128)
    feat_matrix = np.vstack(features)
    # 计算多姿态融合中心特征 (Centroid)
    mean_feat = np.mean(feat_matrix, axis=0, keepdims=True)
    mean_feat = (mean_feat / np.linalg.norm(mean_feat)).astype(np.float32)

    # 计算各样本到中心特征的余弦相似度
    sims_to_mean = np.dot(feat_matrix, mean_feat.T).flatten()
    for d, s in zip(details, sims_to_mean):
        if d["status"] == "success":
            d["sim_to_centroid"] = float(s)

    db_data = {
        "user_name": user_name,
        "features": features,           # list of ndarray (1, 128)
        "feature_matrix": feat_matrix,   # ndarray (N, 128)
        "mean_feature": mean_feat,       # ndarray (1, 128)
        "sources": sources,             # list of str
        "details": details,
        "count": len(features),
        "created_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }

    # 确保输出目录存在
    os.makedirs(os.path.dirname(os.path.abspath(output_pkl)), exist_ok=True)
    with open(output_pkl, "wb") as f:
        pickle.dump(db_data, f)

    print("\n" + "=" * 50)
    print(f"✅ 特征库生成完成！")
    print(f"👤 归属用户: {user_name}")
    print(f"📁 成功入库样本: {len(features)} / {len(img_files)}")
    print(f"📊 样本与中心特征相似度: 最低 {np.min(sims_to_mean):.3f} | 最高 {np.max(sims_to_mean):.3f} | 平均 {np.mean(sims_to_mean):.3f}")
    print(f"💾 特征库保存路径: {os.path.abspath(output_pkl)}")
    print("=" * 50)
    return output_pkl


def main():
    parser = argparse.ArgumentParser(description="YuNet + SFace 人脸特征库生成器")
    parser.add_argument("--source-dir", default=r"C:\Home\Temp\CameraRoll", help="照片素材目录")
    parser.add_argument("--output", default=r"temp\sface_encodings.pkl", help="输出特征库文件路径")
    parser.add_argument("--user", default="Master", help="用户名称")
    parser.add_argument("--yunet", default=None, help="YuNet ONNX 模型路径")
    parser.add_argument("--sface", default=None, help="SFace ONNX 模型路径")
    args = parser.parse_args()

    build_face_database(
        source_dir=args.source_dir,
        output_pkl=args.output,
        user_name=args.user,
        yunet_path=args.yunet,
        sface_path=args.sface
    )


if __name__ == "__main__":
    main()
