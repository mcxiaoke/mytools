import os
import urllib.request
import cv2
import numpy as np

# 1. 自动下载最新的 YuNet (检测) 和 SFace (识别) ONNX 模型（国内镜像加速）
MODELS = {
    "yunet.onnx": "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
    "sface.onnx": "https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx",
}

for name, url in MODELS.items():
  if not os.path.exists(name):
    print(f"正在下载现代轻量模型 {name} ...")
    try:
      urllib.request.urlretrieve(url, name)
    except Exception:
      # 备用直连地址
      raw_url = url.replace("https://mirror.ghproxy.com/", "")
      urllib.request.urlretrieve(raw_url, name)

# 2. 初始化检测器与识别器
detector = cv2.FaceDetectorYN.create("yunet.onnx", "", (640, 480), 0.8, 0.3)
recognizer = cv2.FaceRecognizerSF.create("sface.onnx", "")


def extract_feature(img):
  h, w, _ = img.shape
  detector.setInputSize((w, h))
  _, faces = detector.detect(img)
  if faces is None:
    return None, None
  # faces[0] 是置信度最高的人脸，alignCrop 会自动根据五官关键点把歪头人脸摆正！
  aligned_face = recognizer.alignCrop(img, faces[0])
  feature = recognizer.feature(aligned_face)
  return feature, faces[0]


# 3. 加载你的底库照片（替换成你刚才那张完美照片的文件名）
ref_img = cv2.imread("WIN_20261029_16_43_08_Pro.jpg")
ref_feat, _ = extract_feature(ref_img)
if ref_feat is None:
  raise RuntimeError("底库照片未检测到人脸！")
print("底库特征提取成功！正在打开摄像头...")

# 4. 打开摄像头实时比对
cap = cv2.VideoCapture(0)
while True:
  ret, frame = cap.read()
  if not ret:
    break

  feat, face_box = extract_feature(frame)
  if feat is not None:
    # 计算余弦相似度 (Cosine Similarity)，官方标准阈值：>= 0.363 即为同一人
    score = recognizer.match(
        ref_feat, feat, cv2.FaceRecognizerSF_FR_COSINE
    )
    is_me = score >= 0.363

    x, y, w, h = list(map(int, face_box[:4]))
    color = (0, 255, 0) if is_me else (0, 0, 255)
    label = f"{'ME' if is_me else 'Unknown'} (Score: {score:.2f})"
    cv2.rectangle(frame, (x, y), (x + w, y + h), color, 2)
    cv2.putText(
        frame, label, (x, y - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2
    )

  cv2.imshow("Modern Face Recognition (YuNet + SFace)", frame)
  if cv2.waitKey(1) & 0xFF == ord("q"):
    break

cap.release()
cv2.destroyAllWindows()