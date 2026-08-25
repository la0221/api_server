"""Extract crops from stable-area errors → append to data_stable_crops rehearsal.

Input : D:/incoming/crnn結果/errors/{mold}/gt_{XX}__pred_{YY}/*.png
Output: D:/Content_lens_OCR/data_stable_crops/train/{gt_class}/*.png
        (suffix: __stableerr_{m|x}.png to distinguish from __err93_ batch)

Also visualizes first 4 mohao crops from each mold to make sure they look
sane before we retrain — writes to .ai/records/2026-07/2026-07-27/preview_stableerr/
"""
from __future__ import annotations
import re, sys, shutil
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np

REPO = Path(r"D:/Content_lens_OCR")
sys.path.insert(0, str(REPO / "OCR" / "crnn_fallback"))
from crnn_dataset import crop_band  # noqa: E402
from ultralytics import YOLO  # noqa: E402

import argparse
DEFAULT_SRC = Path(r"D:/incoming/crnn結果/errors")
DST = REPO / "data_stable_crops" / "train"
DET_WEIGHTS = REPO / "OCR/crnn_fallback/runs/detector/weights/best.pt"
DEFAULT_PREVIEW = REPO / ".ai/records/2026-07/2026-07-27/preview_stableerr"
HALF_W = 100
DET_CONF = 0.10

RE_NAME = re.compile(r"^(?P<mo>M[0-9]+)-(?P<xu>[0-9]+)_(?P<rest>.+)_p(?P<rot>0|90)\.png$")


def imread_unicode(p: Path):
    data = np.fromfile(str(p), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_unicode(path: Path, img: np.ndarray) -> bool:
    ok, buf = cv2.imencode(".png", img)
    if not ok:
        return False
    buf.tofile(str(path))
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=str(DEFAULT_SRC))
    ap.add_argument("--suffix", default="stableerr", help="filename suffix tag")
    ap.add_argument("--preview_dir", default=str(DEFAULT_PREVIEW))
    args = ap.parse_args()
    SRC = Path(args.src)
    SUFFIX = args.suffix
    PREVIEW_DIR = Path(args.preview_dir)

    if not SRC.exists():
        print(f"[fatal] no source: {SRC}"); sys.exit(1)
    if PREVIEW_DIR.exists():
        shutil.rmtree(PREVIEW_DIR)
    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)

    det = YOLO(str(DET_WEIGHTS))
    per_class = Counter()
    preview_by_mold = defaultdict(list)
    total_strips = 0
    fail_no_box = []
    for p in SRC.rglob("*.png"):
        m = RE_NAME.match(p.name)
        if not m:
            continue
        gd = m.groupdict()
        gt_mo, gt_xu = gd["mo"], gd["xu"]
        rest = gd["rest"]; rot = gd["rot"]
        total_strips += 1
        img = imread_unicode(p)
        if img is None or img.shape[0] != 640 or img.shape[1] != 640:
            continue
        r = det.predict(img, verbose=False, device=0, conf=DET_CONF)[0]
        if r.boxes is None or len(r.boxes) == 0:
            fail_no_box.append(str(p))
            continue
        cls_arr = r.boxes.cls.cpu().numpy().astype(int)
        confs = r.boxes.conf.cpu().numpy()
        xy = r.boxes.xywh.cpu().numpy()
        band = crop_band(img)
        W = band.shape[1]

        m_idx = np.where(cls_arr == 0)[0]
        if len(m_idx) > 0:
            best = m_idx[int(np.argmax(confs[m_idx]))]
            cx = int(round(float(xy[best][0])))
            crop = band[:, np.arange(cx - HALF_W, cx + HALF_W) % W]
            out_dir = DST / gt_mo
            out_dir.mkdir(parents=True, exist_ok=True)
            out_path = out_dir / f"{gt_mo}-{gt_xu}_{rest}_p{rot}__{SUFFIX}_m.png"
            if imwrite_unicode(out_path, crop):
                per_class[gt_mo] += 1
                if len(preview_by_mold[gt_mo]) < 4:
                    preview_by_mold[gt_mo].append(crop)

        x_idx = np.where(cls_arr == 1)[0]
        if len(x_idx) > 0:
            best = x_idx[int(np.argmax(confs[x_idx]))]
            cx = int(round(float(xy[best][0])))
            crop = band[:, np.arange(cx - HALF_W, cx + HALF_W) % W]
            out_dir = DST / gt_xu
            out_dir.mkdir(parents=True, exist_ok=True)
            out_path = out_dir / f"{gt_mo}-{gt_xu}_{rest}_p{rot}__{SUFFIX}_x.png"
            if imwrite_unicode(out_path, crop):
                per_class[gt_xu] += 1

    # write preview montages so user can visually verify crops before retrain
    for mold, crops in preview_by_mold.items():
        if not crops:
            continue
        # stack vertically with 2 px gap
        max_w = max(c.shape[1] for c in crops)
        rows_ = []
        gap = np.full((2, max_w, 3), 128, dtype=np.uint8)
        for i, c in enumerate(crops):
            if c.shape[1] < max_w:
                pad = np.full((c.shape[0], max_w - c.shape[1], 3), 255, dtype=np.uint8)
                c = np.hstack([c, pad])
            rows_.append(c)
            if i < len(crops) - 1:
                rows_.append(gap)
        stack = np.vstack(rows_)
        imwrite_unicode(PREVIEW_DIR / f"{mold}_mohao_first4.png", stack)

    print(f"processed strips: {total_strips}")
    print(f"failed (no boxes): {len(fail_no_box)}")
    print(f"crops saved per class:")
    for k, v in sorted(per_class.items()):
        print(f"  {k}: +{v}")
    print(f"preview: {PREVIEW_DIR}")


if __name__ == "__main__":
    main()
