"""V9.4 穴號 = A 軸：全量從頭重訓（yolov8s-cls，不 warm-start）。
  data = data_v94/xuehao（data_v92n 平衡化[每類≤360斷04吸收槽] + v9combo_errors 難例；19類含NG）
  環狀 warpPolar(R_INNER=0.6) + XuehaoMixedTierDataset。固定 seed + deterministic。

依 2026-07-27 討論：修 v9.3 的 04 吸收槽,靠「類別平衡 + 全量重訓」,不 fine-tune。
  模號不動（沿用 v9.2）。

Run: python _train_v94_xuehao.py --device 0 --workers 4
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
for p in (REPO / "OCR" / "yolo_a_V6", REPO / "OCR" / "yolo_a_V6.6.4", REPO / "OCR" / "yolo_a_V6.7",
          REPO / "OCR" / "yolo_a_V6.7.1", REPO / "OCR" / "yolo_a_V6.7.3"):
    sys.path.insert(0, str(p))

from ultralytics.models.yolo.classify.train import ClassificationTrainer
from v673_dataset import XuehaoMixedTierDataset

DATA = REPO / "data_v94" / "xuehao"
BASE = REPO / "yolov8s-cls.pt"


def make_trainer():
    class Trainer(ClassificationTrainer):
        def build_dataset(self, img_path, mode="train", batch=None):
            return XuehaoMixedTierDataset(root=img_path, args=self.args,
                                          augment=(mode == "train"), prefix=mode)
    return Trainer


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--epochs", type=int, default=20)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--device", default="0")
    ap.add_argument("--lr0", type=float, default=5e-4)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--patience", type=int, default=8)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()
    if not DATA.exists():
        raise FileNotFoundError("data_v94/xuehao missing（先跑 _build_v94_xuehao.py）")
    if not BASE.exists():
        raise FileNotFoundError(f"base backbone missing: {BASE}")

    overrides = dict(
        model=str(BASE), data=str(DATA), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=args.device, workers=args.workers,
        patience=args.patience, project=str(HERE / "runs_v94"), name="xuehao", exist_ok=True,
        optimizer="AdamW", lr0=args.lr0, lrf=0.1, warmup_epochs=0.0, cos_lr=True,
        seed=args.seed, deterministic=True,
        degrees=0.0, fliplr=0.0, flipud=0.0, scale=0.0, translate=0.0,
        hsv_h=0.0, hsv_s=0.0, hsv_v=0.0, erasing=0.0, auto_augment=None,
        mixup=0.0, cutmix=0.0,
    )
    print(f"[V9.4 xuehao] 全量從頭重訓（平衡化+難例；19類含NG；seed={args.seed} deterministic）")
    trainer = make_trainer()(overrides=overrides)
    trainer.train()
    print(f"[V9.4 xuehao] best -> {trainer.best}")


if __name__ == "__main__":
    main()
