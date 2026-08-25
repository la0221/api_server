"""V9.3 穴號守門(環狀 0.6 + 2-pass,比照部署):
  1) 新字體 holdout(36,未進訓練)→ 是否讀回 04
  2) 舊字體 M28_3 + m28-4(665,全 M28-04)→ 是否仍 0 錯
  3) data_v93/xuehao val 每類(看 04 有沒有變吸收槽、NG 有沒有保住)

Run: python _eval_v93_xuehao.py --device 0
"""
from __future__ import annotations

import argparse
import math
import sys
from collections import Counter
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6.7"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa
from v67_dataset import R_INNER  # noqa

PAD = 255
W = HERE / "runs_v93" / "xuehao" / "weights" / "best.pt"
HOLD = HERE / "m28_newfont_holdout"
OLD = [REPO / "錯誤M28-04" / "M28_3", REPO / "錯誤M28-04" / "m28-4"]
VAL = REPO / "data_v93" / "xuehao" / "val"


def ann(roi, off, size=640, ri=R_INNER):
    h, w = roi.shape[:2]; cx, cy = w // 2, h // 2
    if off:
        roi = cv2.warpAffine(roi, cv2.getRotationMatrix2D((cx, cy), off, 1.0),
                             (w, h), flags=cv2.INTER_LINEAR, borderValue=(PAD,) * 3)
    r = min(cx, cy); C = 2 * math.pi * r
    pol = cv2.warpPolar(roi, (int(r), int(C)), (cx, cy), r,
                        cv2.INTER_LINEAR + cv2.WARP_POLAR_LINEAR)[:, int(ri * r):]
    return white_pad_square(cv2.transpose(cv2.flip(pol, 1)), size)


def roi_of(f):
    img = imread_unicode(f)
    c = find_circle(img)
    if c is None:
        return None
    cx, cy, r = c
    return white_pad_square(img[max(0, cy - r):cy + r, max(0, cx - r):cx + r], target=2 * r)


def p2(m, strips, dev):
    best = ("?", -1.0)
    for s in strips:
        r = m.predict(s, imgsz=640, verbose=False, device=dev)[0]
        p = r.probs.data.cpu().numpy(); i = int(np.argmax(p))
        if float(p[i]) > best[1]:
            best = (m.names[i], float(p[i]))
    return best


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0")
    args = ap.parse_args()
    from ultralytics import YOLO
    m = YOLO(str(W))

    # 1) 新字體 holdout
    hs = sorted(HOLD.glob("*.jpg")); ok = 0; wrong = Counter(); fail = 0
    for f in hs:
        roi = roi_of(f)
        if roi is None:
            fail += 1; continue
        pred, _ = p2(m, [ann(roi, 0), ann(roi, 90)], args.device)
        ok += pred == "04"
        if pred != "04":
            wrong[pred] += 1
    print(f"[1] 新字體 holdout(未進訓練): 04 讀對 {ok}/{len(hs)}  hough_fail={fail}")
    if wrong:
        print(f"    仍誤判: {dict(wrong)}")

    # 2) 舊字體 M28_3 + m28-4
    olds = [f for d in OLD for f in sorted(d.rglob("*.jpg"))]
    ok2 = 0; wrong2 = Counter(); fail2 = 0
    for f in olds:
        roi = roi_of(f)
        if roi is None:
            fail2 += 1; continue
        pred, _ = p2(m, [ann(roi, 0), ann(roi, 90)], args.device)
        ok2 += pred == "04"
        if pred != "04":
            wrong2[pred] += 1
    print(f"[2] 舊字體 M28_3+m28-4: 04 讀對 {ok2}/{len(olds)}  hough_fail={fail2}")
    if wrong2:
        print(f"    誤判: {dict(wrong2)}")

    # 3) val 每類(04 吸收槽 / NG)
    print(f"[3] data_v93 val 每類(單次環狀):")
    tot = tok = 0; absorb = Counter()
    for cd in sorted(d for d in VAL.iterdir() if d.is_dir()):
        c = cd.name; n = k = 0
        for f in cd.glob("*.jpg"):
            roi = cv2.imread(str(f))
            r = m.predict(ann(roi, 0), imgsz=640, verbose=False, device=args.device)[0]
            p = r.probs.data.cpu().numpy(); pred = m.names[int(np.argmax(p))]
            n += 1; k += pred == c
            if pred != c and pred == "04":
                absorb[c] += 1
        tot += n; tok += k
        flag = " ←NG" if c == "NG" else ""
        print(f"    {c}: {k}/{n} = {k/max(1,n)*100:.1f}%{flag}")
    print(f"    總: {tok}/{tot} = {tok/max(1,tot)*100:.2f}%")
    if absorb:
        print(f"    ⚠ 被吸去 04 的類: {dict(absorb)}")
    else:
        print(f"    ✓ 沒有類被吸去 04")


if __name__ == "__main__":
    main()
