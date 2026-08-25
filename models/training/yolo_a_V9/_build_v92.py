# -*- coding: utf-8 -*-
"""V9.2 模號 = v9.1 + M28-04 現場圖（尚未收完全 第一/三包；排除糊的第二包）。
  M28-04 clean(P1+P3) split：holdout 200 raw，其餘 ROI 進 train/M28（上限）；
  M28-04 _MISMATCH(P1+P3) hard 例全進 train/M28。
Run: python _build_v92.py
"""
from __future__ import annotations
import sys, shutil, glob, os, random
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent; REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

V91_M = REPO / "data_v91" / "mohao"     # 累積基底（含 0707 M28 現場）
V92_M = REPO / "data_v92" / "mohao"
HOLD  = REPO / "data_v92" / "_m28_04_holdout"
SU = REPO / "尚未收完全" / "M28"
CLEAN_04 = [SU/"M28第一包"/"M28"/"04", SU/"M28第三包"/"M28"/"04"]
MM_DIRS  = [SU/"M28第一包"/"_MISMATCH", SU/"M28第三包"/"_MISMATCH"]
CAP_TRAIN_04 = 350   # 控制 M28 類別不過度膨脹

def roi_save(src, out_dir: Path, prefix: str) -> bool:
    img = imread_unicode(src)
    if img is None: return False
    c = find_circle(img)
    if c is None: return False
    cx, cy, r = c
    roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    ok, buf = cv2.imencode(".jpg", roi, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok: return False
    out_dir.mkdir(parents=True, exist_ok=True); buf.tofile(str(out_dir/f"{prefix}{Path(src).stem}__r{r}_1.jpg"))
    return True

def main():
    rng = random.Random(0)
    if not V91_M.exists(): raise SystemExit("需先有 data_v91/mohao（跑過 _build_v91_m28field.py）")
    if V92_M.exists(): print("清除舊", V92_M); shutil.rmtree(V92_M)
    print("[1] 複製 data_v91/mohao → data_v92/mohao ...")
    for split in ("train","val"):
        for cd in sorted((V91_M/split).iterdir()):
            if not cd.is_dir(): continue
            dst = V92_M/split/cd.name; dst.mkdir(parents=True, exist_ok=True)
            for f in cd.glob("*.jpg"): shutil.copy2(f, dst/f.name)

    # 2) clean 04
    clean = []
    for d in CLEAN_04: clean += glob.glob(str(d/"*.jpg"))
    clean = sorted(set(clean)); rng.shuffle(clean)
    hold = clean[:200]; train_clean = clean[200:200+CAP_TRAIN_04]
    print(f"[2] M28-04 clean 共 {len(clean)} → holdout {len(hold)} / train {len(train_clean)}")
    ok = 0
    for f in train_clean: ok += roi_save(f, V92_M/"train"/"M28", "fld04_")
    # holdout raw
    if HOLD.exists(): shutil.rmtree(HOLD)
    HOLD.mkdir(parents=True, exist_ok=True)
    for f in hold: shutil.copy2(f, HOLD/os.path.basename(f))
    print(f"    clean 04 進 train {ok}；holdout raw {len(hold)} -> {HOLD}")

    # 3) _MISMATCH 04 hard 例
    hard = []
    for d in MM_DIRS: hard += glob.glob(str(d/"exp_M28-04_*.jpg"))
    hard = sorted(set(hard)); okh = 0
    for f in hard: okh += roi_save(f, V92_M/"train"/"M28", "hard04_")
    print(f"[3] M28-04 _MISMATCH hard 例 {len(hard)} → 進 train {okh}")

    n = len(list((V92_M/"train"/"M28").glob("*.jpg")))
    print(f"\nM28 train 總計 = {n}（v9.1 基底 + clean04 {ok} + hard04 {okh}）")
    print("OUT ->", V92_M)

if __name__ == "__main__":
    main()
