"""V9.2 = 域隨機化強增強、★不餵現場 M28（測純增強能否泛化到未見外觀）。
  data = data_v9/mohao（同 v9，無現場 M28）；dataset = MohaoDomainRandDataset（強 tier1 jitter）。
  其餘同 v9（yolov8s-cls、seed0、deterministic、不warm-start）。
  ★ 結構比照 v6.7.x：本版自成資料夾 OCR/yolo_a_V9.2/，輸出 runs/mohao。
Run: python _train_v92.py --device 0 --workers 4
"""
from __future__ import annotations
import argparse, sys
from pathlib import Path
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
for p in (REPO/"OCR"/"yolo_a_V6", REPO/"OCR"/"yolo_a_V6.6.4", REPO/"OCR"/"yolo_a_V6.7",
          REPO/"OCR"/"yolo_a_V6.7.1", REPO/"OCR"/"yolo_a_V6.7.3", HERE):
    sys.path.insert(0, str(p))
from ultralytics.models.yolo.classify.train import ClassificationTrainer
from _v92_aug_dataset import MohaoDomainRandDataset

DATA = REPO / "data_v9" / "mohao"          # ★ 原始 data_v9，無現場 M28
BASE = REPO / "yolov8s-cls.pt"

def make_trainer():
    class Trainer(ClassificationTrainer):
        def build_dataset(self, img_path, mode="train", batch=None):
            return MohaoDomainRandDataset(root=img_path, args=self.args,
                                          augment=(mode == "train"), prefix=mode)
    return Trainer

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--epochs", type=int, default=20); ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--imgsz", type=int, default=640); ap.add_argument("--device", default="0")
    ap.add_argument("--lr0", type=float, default=5e-4); ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--patience", type=int, default=8); ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()
    if not DATA.exists(): raise FileNotFoundError("data_v9/mohao missing")
    overrides = dict(
        model=str(BASE), data=str(DATA), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=args.device, workers=args.workers,
        patience=args.patience, project=str(HERE/"runs"), name="mohao", exist_ok=True,
        optimizer="AdamW", lr0=args.lr0, lrf=0.1, warmup_epochs=0.0, cos_lr=True,
        seed=args.seed, deterministic=True,
        degrees=0.0, fliplr=0.0, flipud=0.0, scale=0.0, translate=0.0,
        hsv_h=0.0, hsv_s=0.0, hsv_v=0.0, erasing=0.0, auto_augment=None, mixup=0.0, cutmix=0.0,
    )
    print(f"[V9.2 mohao] 域隨機化強增強、不餵現場M28（seed={args.seed} deterministic）")
    trainer = make_trainer()(overrides=overrides); trainer.train()
    print(f"[V9.2 mohao] best -> {trainer.best}")

if __name__ == "__main__":
    main()
