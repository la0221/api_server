"""Extract 200x80 mohao/xuehao crops from v9.3 error strips.

Input : D:/incoming/v9.3vsrcnn/crnn_errors/**/*.png  (640x640 polar strips)
Output: D:/Content_lens_OCR/data_stable_crops/train/{gt_class}/*.png
        (200x80 crops, gt_class = gt_mo for mohao crop, gt_xu for xuehao crop)

Also writes:
  extract_report.md  — per-strip status
  detect_miss_manifest.csv — strips where detector missed one or both boxes

Skips strips where the current detector still can't find any box; those go
to Round 4 (detector re-training) — but we log them for later.
"""
from __future__ import annotations

import csv
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np

REPO = Path(r"D:/Content_lens_OCR")
sys.path.insert(0, str(REPO / "OCR" / "crnn_fallback"))
from crnn_dataset import crop_band  # noqa: E402  (BAND_TOP=280, BAND_BOTTOM=360)

from ultralytics import YOLO  # noqa: E402

DET_WEIGHTS = REPO / "OCR/crnn_fallback/runs/detector/weights/best.pt"
ERR_ROOT = Path(r"D:/incoming/v9.3vsrcnn/crnn_errors")
DST = REPO / "data_stable_crops" / "train"
OUT_DIR = REPO / ".ai/records/2026-07/2026-07-27"
HALF_W = 100

RE_NAME = re.compile(
    r"^gt-(?P<gt_mo>[A-Z0-9]+)-(?P<gt_xu>[A-Z0-9]+)"
    r"__crnn-(?P<pred_mo>[A-Z0-9]+)-(?P<pred_xu>[A-Z0-9]+)"
    r"__(?P<stem>.+)\.png$"
)


def imread_unicode(path: Path) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_unicode(path: Path, img: np.ndarray) -> bool:
    ok, buf = cv2.imencode(".png", img)
    if not ok:
        return False
    buf.tofile(str(path))
    return True


def main():
    if not DET_WEIGHTS.exists():
        print(f"[fatal] detector weights not found: {DET_WEIGHTS}")
        sys.exit(1)
    DST.mkdir(parents=True, exist_ok=True)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    print(f"[load] YOLO detector {DET_WEIGHTS}")
    det = YOLO(str(DET_WEIGHTS))

    strips = sorted(ERR_ROOT.rglob("*.png"))
    print(f"[data] {len(strips)} error strips")

    stats = Counter()
    per_class_saved = defaultdict(int)
    detect_miss = []
    unparsed = []

    for i, p in enumerate(strips):
        m = RE_NAME.match(p.name)
        if not m:
            unparsed.append(str(p))
            stats["unparsed"] += 1
            continue
        gd = m.groupdict()
        gt_mo, gt_xu = gd["gt_mo"], gd["gt_xu"]
        orig_stem = gd["stem"]

        img = imread_unicode(p)
        if img is None:
            stats["read_fail"] += 1
            continue
        if img.shape[0] != 640 or img.shape[1] != 640:
            stats["not_640"] += 1
            continue

        r = det.predict(img, verbose=False, device=0, conf=0.25)[0]
        boxes_present = r.boxes is not None and len(r.boxes) > 0
        if not boxes_present:
            stats["detect_miss_all"] += 1
            detect_miss.append((str(p.relative_to(ERR_ROOT)), "no_boxes", "", ""))
            continue

        cls_arr = r.boxes.cls.cpu().numpy().astype(int)
        conf_arr = r.boxes.conf.cpu().numpy()
        xy_arr = r.boxes.xywh.cpu().numpy()  # (n, 4) cx, cy, w, h
        band = crop_band(img)                # 80x640
        W = band.shape[1]

        saved_m = saved_x = False
        m_idx = np.where(cls_arr == 0)[0]
        x_idx = np.where(cls_arr == 1)[0]

        if len(m_idx) > 0:
            best = m_idx[int(np.argmax(conf_arr[m_idx]))]
            cx = int(round(float(xy_arr[best][0])))
            idxs = np.arange(cx - HALF_W, cx + HALF_W) % W
            crop = band[:, idxs]
            out_dir = DST / gt_mo
            out_dir.mkdir(parents=True, exist_ok=True)
            out_path = out_dir / f"{gt_mo}-{gt_xu}_{orig_stem}__err93_m.png"
            if imwrite_unicode(out_path, crop):
                saved_m = True
                per_class_saved[gt_mo] += 1

        if len(x_idx) > 0:
            best = x_idx[int(np.argmax(conf_arr[x_idx]))]
            cx = int(round(float(xy_arr[best][0])))
            idxs = np.arange(cx - HALF_W, cx + HALF_W) % W
            crop = band[:, idxs]
            out_dir = DST / gt_xu
            out_dir.mkdir(parents=True, exist_ok=True)
            out_path = out_dir / f"{gt_mo}-{gt_xu}_{orig_stem}__err93_x.png"
            if imwrite_unicode(out_path, crop):
                saved_x = True
                per_class_saved[gt_xu] += 1

        if saved_m and saved_x:
            stats["ok_both"] += 1
        elif saved_m:
            stats["ok_m_only"] += 1
            detect_miss.append((str(p.relative_to(ERR_ROOT)), "no_xuehao_box", gt_mo, gt_xu))
        elif saved_x:
            stats["ok_x_only"] += 1
            detect_miss.append((str(p.relative_to(ERR_ROOT)), "no_mohao_box", gt_mo, gt_xu))
        else:
            stats["saved_none"] += 1

        if (i + 1) % 100 == 0:
            print(f"  [{i+1}/{len(strips)}] {dict(stats)}")

    # --- write detect_miss_manifest.csv
    dm_csv = OUT_DIR / "detect_miss_manifest.csv"
    with dm_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["strip_rel_path", "reason", "gt_mo", "gt_xu"])
        w.writerows(detect_miss)

    # --- write extract_report.md
    rep = []
    rep.append("# Extract crops from v9.3 error strips — report\n")
    rep.append(f"\nInput: `{ERR_ROOT}` ({len(strips)} strips)\n")
    rep.append(f"Output: `{DST}`\n\n")
    rep.append("## Stats\n\n")
    rep.append("| status | count |\n|---|---|\n")
    for k, v in stats.most_common():
        rep.append(f"| {k} | {v} |\n")
    rep.append(f"\n**Detect miss manifest**: `detect_miss_manifest.csv` ({len(detect_miss)} rows)\n")

    rep.append("\n## Saved crops per class\n\n")
    rep.append("| class | saved | prior (data_v671_crops_v2/train) | new_total |\n|---|---|---|---|\n")
    real_root = REPO / "data_v671_crops_v2" / "train"
    for cls in sorted(per_class_saved.keys()):
        prior = 0
        prior_dir = real_root / cls
        if prior_dir.exists():
            prior = sum(1 for _ in prior_dir.iterdir() if _.suffix == ".png")
        rep.append(f"| {cls} | {per_class_saved[cls]} | {prior} | {prior + per_class_saved[cls]} |\n")

    if unparsed:
        rep.append("\n## Unparsed (first 20)\n\n")
        for u in unparsed[:20]:
            rep.append(f"- `{u}`\n")

    (OUT_DIR / "extract_report.md").write_text("".join(rep), encoding="utf-8")

    print()
    print(f"[done] {dict(stats)}")
    print(f"[report] {OUT_DIR / 'extract_report.md'}")
    print(f"[detect_miss] {dm_csv}  ({len(detect_miss)} rows)")


if __name__ == "__main__":
    main()
