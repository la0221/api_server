# -*- coding: utf-8 -*-
"""確認資料集影像可解碼 + Hough 命中（用 imread_unicode 支援中文路徑）。"""
import os, glob
import cv2
import numpy as np

ROOT = r"D:\Toro_Project\VISION\AIVision\隱眼專案"
HOUGH = dict(dp=1, minDist=100, param1=200, param2=30, minRadius=200, maxRadius=300)


def imread_unicode(path):
    arr = np.fromfile(path, dtype=np.uint8)
    return cv2.imdecode(arr, cv2.IMREAD_COLOR)


for mold in ["M101", "M60", "M95"]:
    for cav in ["08", "03", "11"]:
        d = os.path.join(ROOT, mold, cav)
        fs = sorted(glob.glob(os.path.join(d, "*.jpg")))
        if not fs:
            print(f"{mold}/{cav}: no jpg")
            continue
        im = imread_unicode(fs[0])
        if im is None:
            print(f"{mold}/{cav}: decode failed")
            continue
        g = cv2.medianBlur(cv2.cvtColor(im, cv2.COLOR_BGR2GRAY), 3)
        c = cv2.HoughCircles(g, cv2.HOUGH_GRADIENT, **HOUGH)
        ok = f"r={int(max(c[0], key=lambda x: x[2])[2])}" if c is not None else "NO CIRCLE"
        print(f"{mold}/{cav}: {os.path.basename(fs[0])} shape={im.shape} hough={ok} n={len(fs)}")
