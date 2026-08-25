"""M1 gate 非 AR OCR — 三場景，per-position argmax + decode."""
from __future__ import annotations
import argparse, sys
from collections import Counter
from pathlib import Path
import cv2, numpy as np, torch
from torch.utils.data import DataLoader, Dataset

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from crnn_dataset import imread_unicode, crop_band, to_tensor
from nonar_model import NonAROCR, decode_padded, encode_padded, NUM_CLASSES, N_QUERIES
from _train_nonar import NonARCropDataset, collate

REPO = HERE.parents[1]
REAL = REPO / "data_v671_crops_v2"
STABLE = Path("D:/incoming/模號穴號-穩定圖片區/前處理區")
DETECTOR = HERE / "runs" / "detector" / "weights" / "best.pt"
NONAR_PT = HERE / "runs" / "nonar_holdout_M54" / "best.pt"
HOLDOUT = "M54"
HALF_W = 100


class M54ValDS(Dataset):
    def __init__(self, root):
        self.samples = sorted(root.glob("*_m.png"))
        self.y = torch.tensor(encode_padded("M54"), dtype=torch.long)
    def __len__(self): return len(self.samples)
    def __getitem__(self, i):
        p = self.samples[i]
        img = imread_unicode(p)
        return to_tensor(img), self.y, "M54"


class StripsPipe(Dataset):
    def __init__(self, root, label_str, detector):
        self.label = label_str
        self.y = torch.tensor(encode_padded(label_str), dtype=torch.long)
        self.crops = []
        strips = sorted(p for p in root.rglob("*.png") if p.is_file())
        B = 32
        for start in range(0, len(strips), B):
            batch = strips[start:start + B]
            imgs = [imread_unicode(p) for p in batch]
            results = detector.predict(imgs, conf=0.25, verbose=False, device=0)
            for img, r in zip(imgs, results):
                if r.boxes is None or len(r.boxes) == 0: continue
                cls_arr = r.boxes.cls.cpu().numpy().astype(int)
                conf_arr = r.boxes.conf.cpu().numpy()
                xy_arr = r.boxes.xywh.cpu().numpy()
                m_idx = np.where(cls_arr == 0)[0]
                if len(m_idx) == 0: continue
                best = m_idx[np.argmax(conf_arr[m_idx])]
                cx = int(round(xy_arr[best][0]))
                band = crop_band(img)
                W = band.shape[1]
                idx = np.arange(cx - HALF_W, cx + HALF_W) % W
                self.crops.append(band[:, idx])
        print(f"  [pipeline] extracted {len(self.crops)} mohao crops")
    def __len__(self): return len(self.crops)
    def __getitem__(self, i):
        return to_tensor(self.crops[i]), self.y, self.label


@torch.no_grad()
def run(name, loader, model, device, show_wrong=False):
    model.eval()
    correct = total = 0
    wrong = []
    for x, _, strs in loader:
        x = x.to(device)
        pred = model(x).argmax(-1).cpu().numpy()  # (B, 4)
        for i, s in enumerate(strs):
            p = decode_padded(pred[i].tolist())
            total += 1
            if p == s:
                correct += 1
            else:
                wrong.append((s, p))
    acc = correct / max(total, 1)
    print(f"[{name}] {correct}/{total} = {acc*100:.2f}%")
    if show_wrong and wrong:
        pd = Counter(p for _, p in wrong)
        print(f"  wrong pred dist: {pd.most_common(10)}")
        for t, p in wrong[:8]:
            print(f"    true={t}  pred={p!r}")
    return total, correct


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--weights", default=str(NONAR_PT))
    args = ap.parse_args()

    device = torch.device(f"cuda:{args.device}" if torch.cuda.is_available() else "cpu")
    ckpt = torch.load(args.weights, map_location=device, weights_only=False)
    model = NonAROCR(num_classes=NUM_CLASSES).to(device)
    model.load_state_dict(ckpt["model"])
    print(f"[eval] loaded {args.weights} ep={ckpt['epoch']} trained_val={ckpt['val_acc']*100:.2f}%")

    val_A = NonARCropDataset(REAL, "val", exclude=[HOLDOUT], aug_shift=0)
    lA = DataLoader(val_A, batch_size=args.batch, shuffle=False,
                    num_workers=args.workers, collate_fn=collate)
    print(f"\n=== (A) in-domain val (excl M54, N={len(val_A)}) ===")
    tA, cA = run("A", lA, model, device, show_wrong=True)

    m54_val = REAL / "val" / HOLDOUT
    if m54_val.exists():
        ds_B = M54ValDS(m54_val)
        lB = DataLoader(ds_B, batch_size=args.batch, shuffle=False,
                        num_workers=0, collate_fn=collate)
        print(f"\n=== (B) M54 val (N={len(ds_B)}) ===")
        tB, cB = run("B", lB, model, device, show_wrong=True)
    else:
        tB = cB = 0

    stable_m54 = STABLE / HOLDOUT
    if stable_m54.exists():
        from ultralytics import YOLO
        detector = YOLO(str(DETECTOR))
        ds_C = StripsPipe(stable_m54, HOLDOUT, detector)
        lC = DataLoader(ds_C, batch_size=args.batch, shuffle=False,
                        num_workers=0, collate_fn=collate)
        print(f"\n=== (C) cross-source 前處理區/M54 ===")
        tC, cC = run("C", lC, model, device, show_wrong=True)
    else:
        tC = cC = 0

    print("\n" + "=" * 60)
    print("M1 GATE (nonar) SUMMARY")
    print("=" * 60)
    print(f"  (A):  {cA}/{tA} = {cA/max(tA,1)*100:.2f}%")
    print(f"  (B):  {cB}/{tB} = {cB/max(tB,1)*100:.2f}%")
    print(f"  (C):  {cC}/{tC} = {cC/max(tC,1)*100:.2f}%")
    b = cB / max(tB, 1)
    v = "★ PASS" if b >= 0.90 else "⚠ BORDERLINE" if b >= 0.60 else "✗ FAIL"
    print(f"  → M1 verdict: {v}")


if __name__ == "__main__":
    main()
