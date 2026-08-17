# -*- coding: utf-8 -*-
"""edge 端「真 CRNN 前處理」的自足、免 torch 版（to_strip）。

★只用 numpy + cv2，不 import torch、不依賴 D:/OCR_demo。edge 只做前處理、不做辨識，
  所以刻意不碰 torch——辨識(YOLO+CRNN)才在 server。這樣 edge 隨便一台有 cv2 的機器就能跑。

流程（= crnn_infer.CrnnReader.to_strip 的等價）：
  raw 鏡片圖 → find_circle(Hough) → 裁圓 white_pad_square → annulus_polar(極座標展開) → 640×640 strip
server 端對這張 strip 用 read(is_strip=True) 只做辨識。

內容是 CRNN 前處理三函式的忠實複製（純 cv2）：
  white_pad_square / find_circle 來源 = D:/OCR_demo/models/crnn/v6_preprocess.py
  annulus_polar      來源 = D:/OCR_demo/models/crnn/crnn_dataset.py（do_rotate=False 路徑）
常數與原檔一致（PAD_VALUE=255、R_INNER=0.6、IMGSZ=640），不可改。
"""
import math

import cv2
import numpy as np

IMGSZ = 640
PAD_VALUE = 255
R_INNER = 0.6


def white_pad_square(img, target=IMGSZ):
    """複製自 v6_preprocess.white_pad_square（純 cv2）。"""
    h, w = img.shape[:2]
    scale = target / max(h, w)
    if scale != 1.0:
        new_w, new_h = int(round(w * scale)), int(round(h * scale))
        img = cv2.resize(img, (new_w, new_h), interpolation=cv2.INTER_AREA)
        h, w = new_h, new_w
    top = (target - h) // 2
    bottom = target - h - top
    left = (target - w) // 2
    right = target - w - left
    return cv2.copyMakeBorder(img, top, bottom, left, right,
                              cv2.BORDER_CONSTANT, value=(PAD_VALUE,) * 3)


def find_circle(img):
    """複製自 v6_preprocess.find_circle（純 cv2 Hough）。回 (cx,cy,r) 或 None。"""
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    gray = cv2.medianBlur(gray, 3)
    circles = cv2.HoughCircles(
        gray, cv2.HOUGH_GRADIENT, dp=1, minDist=100,
        param1=200, param2=30, minRadius=200, maxRadius=300)
    if circles is None:
        return None
    circles = np.uint16(np.around(circles))
    biggest = max(circles[0], key=lambda c: c[2])
    return int(biggest[0]), int(biggest[1]), int(biggest[2])


def annulus_polar(roi, size=IMGSZ, r_inner=R_INNER):
    """複製自 crnn_dataset.annulus_polar 的 do_rotate=False 路徑（純 cv2）。"""
    h, w = roi.shape[:2]
    cx, cy = w // 2, h // 2
    r = min(cx, cy)
    C = 2 * math.pi * r
    pol = cv2.warpPolar(roi, (int(r), int(C)), (cx, cy), r,
                        cv2.INTER_LINEAR + cv2.WARP_POLAR_LINEAR)
    r0 = int(r_inner * r)
    pol = pol[:, r0:]
    return white_pad_square(cv2.transpose(cv2.flip(pol, 1)), size)


def to_strip(img):
    """raw 鏡片圖 → 640×640 strip。回 (strip, hough_used)。
    Hough 失敗 → (white_pad_square(img), False)，與 crnn_infer.to_strip 一致。"""
    c = find_circle(img)
    if c is None:
        return white_pad_square(img, IMGSZ), False
    cx, cy, r = c
    x0, y0 = max(0, cx - r), max(0, cy - r)
    x1, y1 = min(img.shape[1], cx + r), min(img.shape[0], cy + r)
    roi = white_pad_square(img[y0:y1, x0:x1], target=2 * r)
    return annulus_polar(roi, IMGSZ), True
