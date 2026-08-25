"""Re-run 896 v9.3 error strips with new CRNN weights → count recoveries.

For each strip:
  gt from filename (`gt-M83-01__crnn-M88-01__...`) → true (mo, xu)
  new prediction via _pipeline_one on the 640x640 preprocessed strip
  compare → recovered / still_wrong / newly_wrong / detect_still_miss

Reports:
  - overall recovery rate
  - per-mold breakdown
  - per-error-type breakdown
  - top confusion patterns after retrain
"""
from __future__ import annotations

import argparse, re, sys
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np
import torch

REPO = Path(r"D:/Content_lens_OCR")
sys.path.insert(0, str(REPO / "OCR" / "crnn_fallback"))
from crnn_dataset import crop_band, to_tensor  # noqa: E402
from nonar_model import NonAROCR, decode_padded, BLANK_ID, NUM_CLASSES, N_QUERIES  # noqa: E402
from ultralytics import YOLO  # noqa: E402

ERR_ROOT = Path(r"D:/incoming/v9.3vsrcnn/crnn_errors")
DET_WEIGHTS = REPO / "OCR/crnn_fallback/runs/detector/weights/best.pt"
DEFAULT_WEIGHTS = REPO / "OCR/crnn_fallback/runs/nonar_v931_fix/best.pt"
HALF_W = 100

RE_NAME = re.compile(
    r"^gt-(?P<gt_mo>[A-Z0-9]+)-(?P<gt_xu>[A-Z0-9]+)"
    r"__crnn-(?P<pred_mo>[A-Z0-9]+)-(?P<pred_xu>[A-Z0-9]+)"
    r"__(?P<stem>.+)\.png$"
)


def imread_unicode(path: Path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


@torch.no_grad()
def infer_strip(strip: np.ndarray, det, model, device):
    """Return (pred_mo, pred_xu). ? if missing."""
    r = det.predict(strip, verbose=False, device=0, conf=0.25)[0]
    pred_m, pred_x = "?", "?"
    if r.boxes is None or len(r.boxes) == 0:
        return pred_m, pred_x
    cls_arr = r.boxes.cls.cpu().numpy().astype(int)
    conf_arr = r.boxes.conf.cpu().numpy()
    xy_arr = r.boxes.xywh.cpu().numpy()
    band = crop_band(strip)
    W = band.shape[1]
    crops, heads = [], []
    m_idx = np.where(cls_arr == 0)[0]
    x_idx = np.where(cls_arr == 1)[0]
    if len(m_idx) > 0:
        best = m_idx[int(np.argmax(conf_arr[m_idx]))]
        cx = int(round(float(xy_arr[best][0])))
        crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
        heads.append("m")
    if len(x_idx) > 0:
        best = x_idx[int(np.argmax(conf_arr[x_idx]))]
        cx = int(round(float(xy_arr[best][0])))
        crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
        heads.append("x")
    if not crops:
        return pred_m, pred_x
    xin = torch.stack([to_tensor(c) for c in crops], 0).to(device)
    logits = model(xin)
    preds = logits.argmax(-1).cpu().numpy()
    for i, h in enumerate(heads):
        s = decode_padded(preds[i].tolist())
        if h == "m":
            pred_m = s or "?"
        else:
            pred_x = s or "?"
    return pred_m, pred_x


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default=str(DEFAULT_WEIGHTS))
    ap.add_argument("--device", default="0")
    args = ap.parse_args()

    device = torch.device(f"cuda:{args.device}" if torch.cuda.is_available() else "cpu")
    ckpt = torch.load(args.weights, map_location=device, weights_only=False)
    model = NonAROCR(num_classes=NUM_CLASSES).to(device)
    model.load_state_dict(ckpt["model"])
    model.eval()
    print(f"[eval] weights={args.weights} ep={ckpt['epoch']} trained_val={ckpt['val_acc']*100:.2f}%")

    det = YOLO(str(DET_WEIGHTS))

    strips = sorted(ERR_ROOT.rglob("*.png"))
    print(f"[data] {len(strips)} error strips")

    # counters
    total = 0
    recovered_full = 0        # both mo+xu match gt
    recovered_partial_m = 0   # only mohao fixed
    recovered_partial_x = 0   # only xuehao fixed
    still_wrong = 0           # neither fixed
    detect_still_miss = 0     # detector still no box → ? in output

    by_mold_total = Counter()
    by_mold_recovered = Counter()
    by_type_total = Counter()   # original error type
    by_type_recovered = Counter()

    new_conf = Counter()  # (gt, new_pred) that are still wrong

    for i, p in enumerate(strips):
        m = RE_NAME.match(p.name)
        if not m:
            continue
        gd = m.groupdict()
        gt_mo, gt_xu = gd["gt_mo"], gd["gt_xu"]
        orig_pred_mo, orig_pred_xu = gd["pred_mo"], gd["pred_xu"]

        # original error type
        if orig_pred_mo == "NA":
            orig_type = "detector_miss"
        elif orig_pred_mo != gt_mo and orig_pred_xu != gt_xu:
            orig_type = "both_misread"
        elif orig_pred_mo != gt_mo:
            orig_type = "mohao_misread"
        elif orig_pred_xu != gt_xu:
            orig_type = "xuehao_misread"
        else:
            orig_type = "unknown"

        img = imread_unicode(p)
        if img is None or img.shape[0] != 640 or img.shape[1] != 640:
            continue

        new_m, new_x = infer_strip(img, det, model, device)
        total += 1
        by_mold_total[gt_mo] += 1
        by_type_total[orig_type] += 1

        m_ok = new_m == gt_mo
        x_ok = new_x == gt_xu
        if m_ok and x_ok:
            recovered_full += 1
            by_mold_recovered[gt_mo] += 1
            by_type_recovered[orig_type] += 1
        elif m_ok:
            recovered_partial_m += 1
        elif x_ok:
            recovered_partial_x += 1
        else:
            still_wrong += 1

        if new_m == "?" or new_x == "?":
            detect_still_miss += 1

        if not (m_ok and x_ok):
            new_conf[(f"{gt_mo}-{gt_xu}", f"{new_m}-{new_x}")] += 1

        if (i + 1) % 100 == 0:
            print(f"  [{i+1}/{len(strips)}] recovered={recovered_full} still_wrong={still_wrong}")

    # ---- report
    def pct(n, d):
        return f"{n}/{d} ({n/max(d,1)*100:.1f}%)"

    print()
    print("=" * 60)
    print("RECOVERY SUMMARY")
    print("=" * 60)
    print(f"  Total re-evaluated:  {total}")
    print(f"  Fully recovered:     {pct(recovered_full, total)}")
    print(f"  Partial (mo fixed):  {pct(recovered_partial_m, total)}")
    print(f"  Partial (xu fixed):  {pct(recovered_partial_x, total)}")
    print(f"  Still wrong (both):  {pct(still_wrong, total)}")
    print(f"  Detector still miss: {pct(detect_still_miss, total)}")
    print()
    print("Per-mold recovery:")
    for mold in sorted(by_mold_total):
        print(f"  {mold:6}  {pct(by_mold_recovered[mold], by_mold_total[mold])}")
    print()
    print("Per-original-error-type recovery:")
    for t in sorted(by_type_total):
        print(f"  {t:20}  {pct(by_type_recovered[t], by_type_total[t])}")
    print()
    print("Top-20 remaining confusions (gt → new_pred):")
    for (gt, pr), c in new_conf.most_common(20):
        print(f"  {gt:12} → {pr:12}  x{c}")


if __name__ == "__main__":
    main()
