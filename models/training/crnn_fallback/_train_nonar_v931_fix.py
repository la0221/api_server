"""v9.3 field-error rehearsal retrain.

Same code path as `_train_nonar.py` (real + synth + STABLE ConcatDataset), but:
- RUN_DIR → runs/nonar_v931_fix/  (preserves production nonar_include_M54)
- STABLE now populated with 1701 crops extracted from v9.3 error strips
  (D:/incoming/v9.3vsrcnn/crnn_errors/, see .ai/records/2026-07/2026-07-27/)

Goal: fix M83→M88 (297), M28→M23 (26), M17 06→04 (24), M101 xuehao 1↔0 series.
"""
from __future__ import annotations

import argparse, os, sys, time
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader, ConcatDataset

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from nonar_model import NonAROCR, NUM_CLASSES, N_QUERIES  # noqa: E402
from _train_nonar import (  # noqa: E402
    NonARCropDataset, collate, evaluate, set_seed, REAL, SYNTH, STABLE,
)

RUN_DIR = HERE / "runs" / "nonar_v931_fix"
HOLDOUT: list[str] = []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--lr", type=float, default=5e-4)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    os.environ["PYTHONHASHSEED"] = str(args.seed)
    set_seed(args.seed)
    device = torch.device(f"cuda:{args.device}" if torch.cuda.is_available() else "cpu")
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    log_f = open(RUN_DIR / "train.log", "w", encoding="utf-8")
    def log(m):
        print(m); log_f.write(m + "\n"); log_f.flush()

    log(f"[nonar v931-fix] device={device} holdout={HOLDOUT}")
    real_train = NonARCropDataset(REAL, "train", exclude=HOLDOUT)
    synth_train = NonARCropDataset(SYNTH, "train", exclude=HOLDOUT)
    stable_train = NonARCropDataset(STABLE, "train", exclude=HOLDOUT) if STABLE.exists() else None
    parts = [real_train, synth_train]
    if stable_train is not None and len(stable_train) > 0:
        parts.append(stable_train)
    train_ds = ConcatDataset(parts)
    val_ds = NonARCropDataset(REAL, "val", exclude=HOLDOUT, aug_shift=0)
    log(f"[data] real={len(real_train)} synth={len(synth_train)} "
        f"stable={len(stable_train) if stable_train else 0} "
        f"total={len(train_ds)} val={len(val_ds)}")

    train_loader = DataLoader(train_ds, batch_size=args.batch, shuffle=True,
                              num_workers=args.workers, collate_fn=collate,
                              pin_memory=True, drop_last=True)
    val_loader = DataLoader(val_ds, batch_size=args.batch, shuffle=False,
                            num_workers=args.workers, collate_fn=collate,
                            pin_memory=True)

    model = NonAROCR(num_classes=NUM_CLASSES).to(device)
    log(f"[model] NonAROCR params={sum(p.numel() for p in model.parameters())/1e6:.2f}M queries={N_QUERIES}")

    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)

    best = -1.0
    for ep in range(1, args.epochs + 1):
        model.train()
        t0 = time.time(); tot = 0.0; nb = 0
        for x, y, _ in train_loader:
            x = x.to(device, non_blocking=True); y = y.to(device)
            logits = model(x)
            loss = F.cross_entropy(logits.reshape(-1, logits.size(-1)),
                                   y.reshape(-1), label_smoothing=0.05)
            opt.zero_grad(); loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt.step()
            tot += float(loss); nb += 1
        sched.step()
        val_acc, val_loss = evaluate(model, val_loader, device)
        log(f"[ep {ep:02d}/{args.epochs}] train_loss={tot/nb:.4f} val_loss={val_loss:.4f} "
            f"val_exact={val_acc:.4f} lr={opt.param_groups[0]['lr']:.2e} time={time.time()-t0:.1f}s")
        if val_acc > best:
            best = val_acc
            torch.save({"model": model.state_dict(), "epoch": ep, "val_acc": val_acc},
                       RUN_DIR / "best.pt")
            log(f"  ↑ best.pt (val_exact={val_acc:.4f})")

    log(f"[done] best={best:.4f}")
    log_f.close()


if __name__ == "__main__":
    main()
