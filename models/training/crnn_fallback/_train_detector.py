"""訓 YOLOv8n text detector on 108 手標 samples。

Aug 關鍵：
- mosaic=0（會拼幾張、破壞文字幾何）
- flipud/fliplr=0（M/6/9 對稱不合、翻了會亂）
- degrees 0（文字方向很敏感）
- 只留輕度色彩 + 平移
"""
from __future__ import annotations
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DATA_YAML = HERE / "detector_data" / "data.yaml"
RUN_DIR = HERE / "runs" / "detector"

REPO = HERE.parents[1]
BASE_WEIGHTS = REPO / "yolov8s-cls.pt"  # V9 提到有 imagenet weights
# YOLOv8n detection 有內建 pretrained: model.train from "yolov8n.pt"

if not DATA_YAML.exists():
    raise FileNotFoundError(DATA_YAML)


def main():
    from ultralytics import YOLO
    model = YOLO("yolov8n.pt")  # detection pretrained
    model.train(
        data=str(DATA_YAML),
        epochs=120,
        imgsz=640,
        batch=16,
        device=0,
        project=str(RUN_DIR.parent),
        name=RUN_DIR.name,
        exist_ok=True,
        seed=0,
        deterministic=True,
        # augmentation — 只留輕度
        mosaic=0.0,
        mixup=0.0,
        copy_paste=0.0,
        fliplr=0.0,
        flipud=0.0,
        degrees=0.0,
        translate=0.05,
        scale=0.1,
        shear=0.0,
        perspective=0.0,
        hsv_h=0.0,
        hsv_s=0.1,
        hsv_v=0.2,
        # 訓練參數
        optimizer="AdamW",
        lr0=1e-3,
        cos_lr=True,
        warmup_epochs=3,
        patience=30,
        workers=4,
    )
    print(f"[done] weights at {RUN_DIR}/weights/best.pt")


if __name__ == "__main__":
    main()
