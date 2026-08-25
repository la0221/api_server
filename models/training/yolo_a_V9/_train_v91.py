"""V9.1 模號 = v9 配方 + 現場 M28 圖（S1 外觀強健化診斷）。
  data = data_v91/mohao（data_v9 + 現場 M28 400 張）。其餘同 v9（yolov8s-cls、seed0、deterministic、不warm-start）。
Run: python _train_v91.py --device 0 --workers 4
"""
from __future__ import annotations
import argparse, sys
from pathlib import Path
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
for p in (REPO/"OCR"/"yolo_a_V6", REPO/"OCR"/"yolo_a_V6.6.4", REPO/"OCR"/"yolo_a_V6.7",
          REPO/"OCR"/"yolo_a_V6.7.1", REPO/"OCR"/"yolo_a_V6.7.3"):
    sys.path.insert(0, str(p))
from ultralytics.models.yolo.classify.train import ClassificationTrainer
from v673_dataset import MohaoMixedTierDataset

DATA = REPO / "data_v91" / "mohao"
BASE = REPO / "yolov8s-cls.pt"

def make_trainer():
    class Trainer(ClassificationTrainer):
        def build_dataset(self, img_path, mode="train", batch=None):
            return MohaoMixedTierDataset(root=img_path, args=self.args,
                                         augment=(mode == "train"), prefix=mode)
    return Trainer

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--epochs", type=int, default=20); ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--imgsz", type=int, default=640); ap.add_argument("--device", default="0")
    ap.add_argument("--lr0", type=float, default=5e-4); ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--patience", type=int, default=8); ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()
    if not DATA.exists(): raise FileNotFoundError("data_v91/mohao missing（先跑 _build_v91_m28field.py）")
    overrides = dict(
        model=str(BASE), data=str(DATA), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=args.device, workers=args.workers,
        patience=args.patience, project=str(HERE/"runs_v91"), name="mohao", exist_ok=True,
        optimizer="AdamW", lr0=args.lr0, lrf=0.1, warmup_epochs=0.0, cos_lr=True,
        seed=args.seed, deterministic=True,
        degrees=0.0, fliplr=0.0, flipud=0.0, scale=0.0, translate=0.0,
        hsv_h=0.0, hsv_s=0.0, hsv_v=0.0, erasing=0.0, auto_augment=None, mixup=0.0, cutmix=0.0,
    )
    print(f"[V9.1 mohao] v9配方+現場M28（seed={args.seed} deterministic）")
    trainer = make_trainer()(overrides=overrides); trainer.train()
    print(f"[V9.1 mohao] best -> {trainer.best}")

if __name__ == "__main__":
    main()
