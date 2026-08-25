"""非 AR OCR 訓練。混 real + synth，holdout M54。"""
from __future__ import annotations

import argparse, os, random, sys, time
from pathlib import Path
import numpy as np
import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader, ConcatDataset, Dataset

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from crnn_dataset import imread_unicode, to_tensor
from nonar_model import (NonAROCR, encode_padded, decode_padded,
                          NUM_CLASSES, N_QUERIES, CHAR2IDX)

REPO = HERE.parents[1]
REAL = REPO / "data_v671_crops_v2"
SYNTH = REPO / "data_v671_crops_v2_synth"
STABLE = REPO / "data_stable_crops"                 # 前處理區 M17/28/83/101 extra real
RUN_DIR = HERE / "runs" / "nonar_include_M54"
HOLDOUT = []  # M54 進訓、放棄 open-vocab、換 M54 精度


def set_seed(s):
    random.seed(s); np.random.seed(s); torch.manual_seed(s)
    torch.cuda.manual_seed_all(s)
    torch.backends.cudnn.deterministic = True
    torch.backends.cudnn.benchmark = False


class NonARCropDataset(Dataset):
    def __init__(self, root, split, exclude=(), aug_shift=8):
        self.aug_shift = aug_shift
        self.samples: list[tuple[Path, str]] = []
        excl = set(exclude)
        for cls in sorted((Path(root) / split).iterdir()):
            if not cls.is_dir() or cls.name in excl:
                continue
            if not all(c in CHAR2IDX for c in cls.name):
                continue
            for p in cls.iterdir():
                if p.suffix.lower() == ".png":
                    self.samples.append((p, cls.name))

    def __len__(self): return len(self.samples)

    def __getitem__(self, i):
        p, s = self.samples[i]
        img = imread_unicode(p)
        if self.aug_shift > 0:
            k = random.randint(-self.aug_shift, self.aug_shift)
            if k != 0:
                img = np.roll(img, k, axis=1)
        x = to_tensor(img)
        y = torch.tensor(encode_padded(s), dtype=torch.long)  # (4,)
        return x, y, s


def collate(batch):
    xs, ys, ss = zip(*batch)
    return torch.stack(xs, 0), torch.stack(ys, 0), list(ss)


@torch.no_grad()
def evaluate(model, loader, device):
    model.eval()
    correct = total = 0; losses = []
    for x, y, strs in loader:
        x = x.to(device); y = y.to(device)
        logits = model(x)  # (B, 4, 12)
        loss = F.cross_entropy(logits.reshape(-1, logits.size(-1)),
                               y.reshape(-1))
        losses.append(float(loss))
        pred = logits.argmax(-1).cpu().numpy()
        for i, s in enumerate(strs):
            if decode_padded(pred[i].tolist()) == s:
                correct += 1
            total += 1
    return correct / max(total, 1), float(np.mean(losses))


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
    def log(m): print(m); log_f.write(m + "\n"); log_f.flush()

    log(f"[nonar] device={device} holdout={HOLDOUT}")
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
