"""Try lowering YOLO confidence threshold on the 90 detect-miss strips.

If a lower threshold recovers a box that lands on the right character (verified
by running CRNN on the crop and checking it matches gt), we get a free win.

Also samples 8 detect-miss strips for visual inspection → helps decide
whether re-labeling or a preprocess fix is warranted.
"""
from __future__ import annotations
import csv, sys, shutil
from collections import Counter
from pathlib import Path

import cv2
import numpy as np
import torch

REPO = Path(r"D:/Content_lens_OCR")
sys.path.insert(0, str(REPO / "OCR" / "crnn_fallback"))
from crnn_dataset import crop_band, to_tensor  # noqa: E402
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES  # noqa: E402
from ultralytics import YOLO  # noqa: E402

ERR_ROOT = Path(r"D:/incoming/v9.3vsrcnn/crnn_errors")
DET_WEIGHTS = REPO / "OCR/crnn_fallback/runs/detector/weights/best.pt"
NONAR_WEIGHTS = REPO / "OCR/crnn_fallback/runs/nonar_v931_fix/best.pt"
DETECT_MISS_CSV = REPO / ".ai/records/2026-07/2026-07-27/detect_miss_manifest.csv"
OUT_DIR = REPO / ".ai/records/2026-07/2026-07-27"
SAMPLES_DIR = OUT_DIR / "detect_miss_samples"
HALF_W = 100


def imread_unicode(p: Path):
    data = np.fromfile(str(p), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def main():
    with DETECT_MISS_CSV.open("r", encoding="utf-8") as f:
        r = csv.DictReader(f)
        rows = list(r)
    print(f"[data] {len(rows)} detect-miss strips")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    det = YOLO(str(DET_WEIGHTS))
    ckpt = torch.load(NONAR_WEIGHTS, map_location=device, weights_only=False)
    model = NonAROCR(num_classes=NUM_CLASSES).to(device)
    model.load_state_dict(ckpt["model"])
    model.eval()

    stats = Counter()
    still_no_box_at_005 = []
    recovered_at_010 = []
    recovered_at_005 = []

    for row in rows:
        strip_path = ERR_ROOT / row["strip_rel_path"].replace("\\", "/")
        reason = row["reason"]
        # parse gt from filename
        # gt-M83-01__crnn-NA-NA__...
        parts = strip_path.name.split("__")
        gt_part = parts[0]  # gt-M83-01
        gt_pieces = gt_part.split("-", 2)
        gt_mo, gt_xu = gt_pieces[1], gt_pieces[2]
        img = imread_unicode(strip_path)
        if img is None:
            stats["read_fail"] += 1; continue

        found_at = None
        pred_at = None
        for conf_thr in (0.10, 0.05):
            r = det.predict(img, verbose=False, device=0, conf=conf_thr)[0]
            if r.boxes is None or len(r.boxes) == 0:
                continue
            cls_arr = r.boxes.cls.cpu().numpy().astype(int)
            confs = r.boxes.conf.cpu().numpy()
            xy = r.boxes.xywh.cpu().numpy()
            band = crop_band(img)
            W = band.shape[1]
            # check what we're missing
            need_m = reason in ("no_mohao_box", "no_boxes")
            need_x = reason in ("no_xuehao_box", "no_boxes")
            crops, heads = [], []
            if need_m:
                m_idx = np.where(cls_arr == 0)[0]
                if len(m_idx):
                    b = m_idx[int(np.argmax(confs[m_idx]))]
                    cx = int(round(float(xy[b][0])))
                    crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
                    heads.append("m")
            if need_x:
                x_idx = np.where(cls_arr == 1)[0]
                if len(x_idx):
                    b = x_idx[int(np.argmax(confs[x_idx]))]
                    cx = int(round(float(xy[b][0])))
                    crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
                    heads.append("x")
            if not crops:
                continue
            xin = torch.stack([to_tensor(c) for c in crops], 0).to(device)
            with torch.no_grad():
                p = model(xin).argmax(-1).cpu().numpy()
            got = {}
            for i, h in enumerate(heads):
                got[h] = decode_padded(p[i].tolist())
            all_ok = True
            if need_m and got.get("m") != gt_mo:
                all_ok = False
            if need_x and got.get("x") != gt_xu:
                all_ok = False
            if all_ok:
                found_at = conf_thr
                pred_at = got
                break

        if found_at == 0.10:
            recovered_at_010.append((str(strip_path), pred_at))
            stats["recovered_at_010"] += 1
        elif found_at == 0.05:
            recovered_at_005.append((str(strip_path), pred_at))
            stats["recovered_at_005"] += 1
        else:
            still_no_box_at_005.append(str(strip_path))
            stats["still_no_box"] += 1

    print()
    print(f"stats: {dict(stats)}")
    print(f"  recovered_at_010: {len(recovered_at_010)}")
    print(f"  recovered_at_005: {len(recovered_at_005)}")
    print(f"  still no valid box even at conf=0.05: {len(still_no_box_at_005)}")

    # sample 8 still-miss for visual inspection
    SAMPLES_DIR.mkdir(parents=True, exist_ok=True)
    for f in SAMPLES_DIR.iterdir():
        f.unlink()
    for s in still_no_box_at_005[:8]:
        shutil.copy(s, SAMPLES_DIR / Path(s).name)
    print(f"  sampled 8 → {SAMPLES_DIR}")

    # write manifest of still-miss for hand-labeling
    stillmiss_csv = OUT_DIR / "detect_still_miss_at_005.csv"
    with stillmiss_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["strip_path"])
        for s in still_no_box_at_005:
            w.writerow([s])
    print(f"  hand-label list → {stillmiss_csv}")


if __name__ == "__main__":
    main()
