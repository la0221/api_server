"""v9 強化重訓（完整聯集 + 域隨機化強增強）。--head mohao|xuehao。
data = data_v9/<head>（完整聯集）；dataset = Mohao/XuehaoStrongDataset；seed0 deterministic 不warm-start。
Run: python _train_v9_strong.py --head mohao --device 0
     python _train_v9_strong.py --head xuehao --device 0
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
from _v9_strong_aug import MohaoStrongDataset, XuehaoStrongDataset
BASE = REPO / "yolov8s-cls.pt"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--head", choices=["mohao","xuehao"], required=True)
    ap.add_argument("--epochs", type=int, default=20); ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--imgsz", type=int, default=640); ap.add_argument("--device", default="0")
    ap.add_argument("--lr0", type=float, default=5e-4); ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--patience", type=int, default=8); ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()
    DATA = REPO / "data_v9" / args.head
    DS = MohaoStrongDataset if args.head=="mohao" else XuehaoStrongDataset
    if not DATA.exists(): raise FileNotFoundError(f"{DATA} missing（先跑 _build_union_v9.py）")
    def make_trainer():
        class Trainer(ClassificationTrainer):
            def build_dataset(self, img_path, mode="train", batch=None):
                return DS(root=img_path, args=self.args, augment=(mode=="train"), prefix=mode)
        return Trainer
    overrides = dict(
        model=str(BASE), data=str(DATA), epochs=args.epochs,
        imgsz=args.imgsz, batch=args.batch, device=args.device, workers=args.workers,
        patience=args.patience, project=str(HERE/"runs"), name=args.head, exist_ok=True,
        optimizer="AdamW", lr0=args.lr0, lrf=0.1, warmup_epochs=0.0, cos_lr=True,
        seed=args.seed, deterministic=True,
        degrees=0.0, fliplr=0.0, flipud=0.0, scale=0.0, translate=0.0,
        hsv_h=0.0, hsv_s=0.0, hsv_v=0.0, erasing=0.0, auto_augment=None, mixup=0.0, cutmix=0.0,
    )
    print(f"[v9 {args.head} 強化] 完整聯集 + 域隨機化強增強（seed={args.seed}）")
    trainer = make_trainer()(overrides=overrides); trainer.train()
    print(f"[v9 {args.head}] best -> {trainer.best}")

if __name__ == "__main__":
    main()
