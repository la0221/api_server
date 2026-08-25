# -*- coding: utf-8 -*-
"""穩定圖片區 3 模驗證：M101 / M17 / M83，每穴取 N=50 張。
find_circle ROI → annulus_polar → 比對 v9 vs v6.7.2 vs v6.7.3 的模號準確率。
真值模號 = 資料夾。Run: python _val_stable_3molds.py --device 0 --n 50
"""
from __future__ import annotations
import argparse, os, sys
from pathlib import Path
from collections import Counter, defaultdict
import cv2, numpy as np
cv2.setNumThreads(0)
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6"))
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6.7"))
from v67_dataset import R_INNER, annulus_polar
from v6_preprocess import imread_unicode, find_circle, white_pad_square

MOLDS = {
 "M101": r"D:\模號穴號-穩定圖片區\M101\M101收圖1\M101",
 "M17":  r"D:\模號穴號-穩定圖片區\M17\M17新的ROI\M17新的ROI_1\M17",
 "M83":  r"D:\模號穴號-穩定圖片區\M83\第二包",
}
WEIGHTS = {
 "v6.7.2": REPO/"OCR"/"yolo_a_V6.7.2"/"runs"/"mohao"/"weights"/"best.pt",
 "v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"mohao"/"weights"/"best.pt",
 "v9":     HERE/"runs"/"mohao"/"weights"/"best.pt",
}
EXT = (".jpg", ".jpeg", ".png", ".bmp")

def cavity_leaves(root):
    """回傳 {cavity_name: [img paths]}；cavity = 影像的直接父夾名。"""
    out = defaultdict(list)
    for dp, dn, fn in os.walk(root):
        imgs = sorted(f for f in fn if f.lower().endswith(EXT))
        if imgs:
            cav = os.path.basename(dp)
            for f in imgs: out[cav].append(os.path.join(dp, f))
    return out

def make_strip(path):
    img = imread_unicode(path)
    if img is None: return None
    circ = find_circle(img)
    if circ is None: return None
    cx, cy, r = circ
    roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    return annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0"); ap.add_argument("--n", type=int, default=50)
    a = ap.parse_args()
    from ultralytics import YOLO
    models = {t: YOLO(str(p)) for t, p in WEIGHTS.items() if p.exists()}
    tags = list(models)
    print(f"模型: {tags}   每穴取樣 N={a.n}\n")

    # 先把每模的取樣圖 ROI 化一次(共用給三模)，避免重複前處理
    summary = {t: {} for t in tags}
    for mold, root in MOLDS.items():
        leaves = cavity_leaves(root)
        cavs = sorted(leaves)
        strips = []; miss = 0; used = 0
        for cav in cavs:
            for p in leaves[cav][:a.n]:
                s = make_strip(p)
                if s is None: miss += 1
                else: strips.append(s); used += 1
        print(f"[{mold}] 穴號 {cavs}  取樣 {used} 張(find_circle 漏 {miss})")
        # 三模各跑
        for t in tags:
            m = models[t]; ok = 0; wrong = Counter()
            for s in strips:
                r = m.predict(s, imgsz=640, verbose=False, device=a.device)[0]
                pr = r.names[int(np.argmax(r.probs.data.cpu().numpy()))]
                if pr == mold: ok += 1
                else: wrong[pr] += 1
            acc = ok/used*100 if used else 0
            summary[t][mold] = (ok, used, acc, dict(wrong))
        print()

    # 彙總表
    print("===== 模號準確率彙總（真值=資料夾）=====")
    hdr = f"{'模號':6s}" + "".join(f"{t:>14s}" for t in tags)
    print(hdr); print("-"*len(hdr))
    for mold in MOLDS:
        row = f"{mold:6s}"
        for t in tags:
            ok, n, acc, _ = summary[t][mold]; row += f"  {ok:4d}/{n:4d}={acc:5.1f}"
        print(row)
    print("\n===== 誤判去向 =====")
    for mold in MOLDS:
        for t in tags:
            ok, n, acc, wrong = summary[t][mold]
            if wrong: print(f"  [{t}] {mold}: {wrong}")

if __name__ == "__main__":
    main()
