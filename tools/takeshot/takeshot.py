# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "opencv-python",
#     "numpy",
# ]
# ///

import os
import sys
import time
import argparse
import tempfile
from datetime import datetime
import cv2
import numpy as np

# 避免 Windows 终端编码问题
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


def parse_args():
    parser = argparse.ArgumentParser(
        description="极简命令行快速拍照工具 (takeshot)",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("-o", "--output", help="输出文件路径或目录 (默认: 系统临时目录 takeshot)")
    parser.add_argument("-w", "--delay", type=float, default=0.0, help="拍照前倒计时等待时长 (秒)")
    parser.add_argument("-n", "--count", type=int, default=1, help="拍照张数")
    parser.add_argument("-i", "--interval", type=float, default=1.0, help="连拍间隔时长 (秒)")
    parser.add_argument(
        "-d", "--duration", type=float, default=0.0, help="总持续拍摄时长 (秒, 设定后自动覆盖固定张数)"
    )
    parser.add_argument("-c", "--camera", type=int, default=0, help="摄像头设备索引编号")
    parser.add_argument("-W", "--width", type=int, default=1920, help="目标画面宽度")
    parser.add_argument("-H", "--height", type=int, default=1080, help="目标画面高度")
    parser.add_argument(
        "--res",
        type=str,
        default=None,
        help="快速指定分辨率预设 (如 1080p, 720p, 4k, 或 1920x1080)",
    )
    parser.add_argument(
        "--warmup",
        type=float,
        default=0.5,
        help="画面就绪后测光与曝光稳定时长 (秒, 默认 0.5s 防过曝惨白)",
    )
    parser.add_argument(
        "--fast",
        action="store_true",
        help="极速模式：跳过测光收敛等待，画面脱离全黑立即触发拍照",
    )
    parser.add_argument(
        "-q", "--quiet", action="store_true", help="静默模式，仅在标准输出打印保存的文件路径"
    )
    return parser.parse_args()


def resolve_resolution(res_str, width, height):
    """解析指定的分辨率预设或自定义宽高"""
    if res_str:
        presets = {
            "4k": (3840, 2160),
            "2k": (2560, 1440),
            "1080p": (1920, 1080),
            "720p": (1280, 720),
            "480p": (640, 480),
        }
        key = res_str.strip().lower()
        if key in presets:
            return presets[key]
        if "x" in key:
            parts = key.split("x")
            if len(parts) == 2 and parts[0].isdigit() and parts[1].isdigit():
                return int(parts[0]), int(parts[1])
    return width, height


def get_target_path(base_output, index, total):
    """计算目标文件路径：支持传入目录或直接传入文件名"""
    now_str = datetime.now().strftime("%Y%m%d_%H%M%S_%f")[:19]
    default_name = f"shot_{now_str}.jpg"

    if not base_output:
        temp_dir = os.path.join(tempfile.gettempdir(), "takeshot")
        os.makedirs(temp_dir, exist_ok=True)
        return os.path.join(temp_dir, default_name)

    base_output = os.path.abspath(base_output)
    if os.path.isdir(base_output) or base_output.endswith(("/", "\\")):
        os.makedirs(base_output, exist_ok=True)
        return os.path.join(base_output, default_name)

    # 传入具体文件路径时，多张自动添加编号后缀
    if total > 1:
        root, ext = os.path.splitext(base_output)
        if not ext:
            ext = ".jpg"
        return f"{root}_{index:03d}{ext}"
    return base_output


def main():
    args = parse_args()

    # 1. 启动延时
    if args.delay > 0:
        if not args.quiet:
            print(f"[takeshot] 等待延时 {args.delay} 秒...")
        time.sleep(args.delay)

    target_w, target_h = resolve_resolution(args.res, args.width, args.height)

    # 2. 一次性以 1080p + MJPG 方式打开摄像头，避免后续反复重建 DirectShow 滤镜图带来延迟
    fourcc = cv2.VideoWriter_fourcc(*"MJPG")
    open_params = [
        cv2.CAP_PROP_FOURCC,
        fourcc,
        cv2.CAP_PROP_FRAME_WIDTH,
        target_w,
        cv2.CAP_PROP_FRAME_HEIGHT,
        target_h,
    ]

    cap = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW, open_params)
    if not cap.isOpened():
        # 兼容性回退：先普通开启再逐项配置
        cap = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW)
        if not cap.isOpened():
            cap = cv2.VideoCapture(args.camera)
        if cap.isOpened():
            cap.set(cv2.CAP_PROP_FOURCC, fourcc)
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, target_w)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, target_h)

    if not cap.isOpened():
        print(f"[错误] 无法打开摄像头 (Index: {args.camera})", file=sys.stderr)
        sys.exit(1)

    try:
        actual_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        actual_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        if not args.quiet:
            print(f"[takeshot] 摄像头已就绪，当前分辨率: {actual_w}x{actual_h}")

        # 3. 硬件测光收敛与曝光预热：
        # 很多 USB 摄像头在通电首帧为全黑，第 1~3 帧自动曝光处于最大增益状态（容易造成惨白过曝）。
        # 默认持续消费帧缓冲区 0.5 秒，等待相机硬件 ISP 的 AE/AWB 自然收敛到正常曝光。
        warmup_duration = 0.0 if args.fast else max(0.0, args.warmup)
        latest_frame = None
        first_valid_time = None

        for _ in range(45):  # 最多保护性循环 45 帧（约 1.5s）
            ret, frame = cap.read()
            if not ret:
                continue

            # 过滤硬件上电初始全黑帧
            if float(np.mean(frame)) <= 1.0:
                continue

            latest_frame = frame
            now = time.perf_counter()
            if first_valid_time is None:
                first_valid_time = now

            # 只要测光收敛时长已达到目标，立即完成预热
            if (now - first_valid_time) >= warmup_duration:
                break

        # 4. 执行拍照逻辑
        saved_files = []
        start_time = time.time()
        idx = 0

        while True:
            # 持续时间模式判断
            if args.duration > 0 and (time.time() - start_time) >= args.duration:
                break
            # 张数模式判断
            if args.duration <= 0 and idx >= args.count:
                break

            # 第一张直接复用预热最后捕获的那张已曝光收敛的帧
            if idx == 0 and latest_frame is not None:
                frame = latest_frame
                ret = True
            else:
                ret, frame = cap.read()

            if not ret:
                print(f"[警告] 抓取第 {idx + 1} 帧失败", file=sys.stderr)
                time.sleep(args.interval)
                continue

            target_path = get_target_path(args.output, idx + 1, args.count)
            os.makedirs(os.path.dirname(target_path), exist_ok=True)

            # 保存高质量 JPEG (画质 95)
            cv2.imwrite(target_path, frame, [cv2.IMWRITE_JPEG_QUALITY, 95])
            saved_files.append(target_path)

            if args.quiet:
                print(target_path)
            else:
                print(f"[takeshot] 照片已保存: {target_path}")

            idx += 1

            # 连拍间隔处理
            need_more = (
                args.duration > 0 and (time.time() - start_time) < args.duration
            ) or (args.duration <= 0 and idx < args.count)
            if need_more and args.interval > 0:
                time.sleep(args.interval)

    finally:
        cap.release()


if __name__ == "__main__":
    main()
