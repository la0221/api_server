"""V9.4 模號 = A 軸：全量從頭重訓（yolov8s-cls，不 warm-start）。
  data = data_v94/mohao（data_v92 平衡化[每類≤360] + v9combo_errors 難例；20類含NG）
  環狀 warpPolar(R_INNER=0.6) + MohaoMixedTierDataset（tier1 含外觀抖動，與 mohao 對齊）。

依 2026-07-27 討論：模號比照穴號同一套（平衡+全量重訓，不 fine-tune），修 M54→M17 等混淆。

Run: python _train_v94_mohao.py --device 0 --workers 4
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
from v673_dataset import MohaoMixedTierDataset

DATA = REPO / "data_v94" / "mohao"
BASE = REPO / "yolov8s-cls.pt"


def make_trainer():
    class Trainer(ClassificationTrainer):
        def build_dataset(self, img_path, mode="train", batch=None):
            return MohaoMixedTierDataset(root=img_path, args=self.args,
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
        raise FileNotFoundError("data_v94/mohao missing（先跑 _build_v94_mohao.py）")
    if not BASE.exists():
        raise FileNotFoundError(f"base backbone missing: {BASE}")

    overrides = dict(
        model=str(BASE), data=str(DATA), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=args.device, workers=args.workers,
        patience=args.patience, project=str(HERE / "runs_v94"), name="mohao", exist_ok=True,
        optimizer="AdamW", lr0=args.lr0, lrf=0.1, warmup_epochs=0.0, cos_lr=True,
        seed=args.seed, deterministic=True,
        degrees=0.0, fliplr=0.0, flipud=0.0, scale=0.0, translate=0.0,
        hsv_h=0.0, hsv_s=0.0, hsv_v=0.0, erasing=0.0, auto_augment=None,
        mixup=0.0, cutmix=0.0,
    )
    print(f"[V9.4 mohao] 全量從頭重訓（平衡化+難例；20類含NG；seed={args.seed} deterministic）")
    trainer = make_trainer()(overrides=overrides)
    trainer.train()
    print(f"[V9.4 mohao] best -> {trainer.best}")


if __name__ == "__main__":
    main()
