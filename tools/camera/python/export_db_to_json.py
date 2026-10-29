#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
人脸特征库跨语言导出工具：将 Python .pkl 转换为 .NET 可直接读取的标准 JSON 格式
"""

import os
import sys
import json
import pickle
import numpy as np

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

def export_pkl_to_json(pkl_path=r"temp\sface_encodings.pkl", json_path=r"temp\sface_encodings.json"):
    pkl_abs = os.path.abspath(pkl_path)
    json_abs = os.path.abspath(json_path)

    if not os.path.exists(pkl_abs):
        raise FileNotFoundError(f"未找到特征库: {pkl_abs}")

    with open(pkl_abs, "rb") as f:
        db = pickle.load(f)

    # 提取并转换为标准 Python float 列表
    mean_feat_list = db["mean_feature"].flatten().astype(float).tolist()
    features_list = [f.flatten().astype(float).tolist() for f in db["features"]]

    json_data = {
        "user_name": db.get("user_name", "Master"),
        "count": len(db["sources"]),
        "feature_dim": 128,
        "sources": db["sources"],
        "mean_feature": mean_feat_list,
        "features": features_list,
        "created_at": db.get("created_at", "")
    }

    os.makedirs(os.path.dirname(json_abs), exist_ok=True)
    with open(json_abs, "w", encoding="utf-8") as f:
        json.dump(json_data, f, ensure_ascii=False, indent=2)

    print(f"✅ 特征库成功转换为跨平台 JSON 格式！")
    print(f"📁 输入 PKL: {pkl_abs}")
    print(f"📄 输出 JSON: {json_abs} (包含 {len(features_list)} 个样本，维度 128)")
    return json_abs

if __name__ == "__main__":
    export_pkl_to_json()
