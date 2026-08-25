# -*- coding: utf-8 -*-
"""S1 診斷：v9.1 = data_v9/mohao + 現場 M28 圖（測試機 2026-07-07）。
  70% 現場 M28 ROI 化進 train/M28；30% 保留 raw 當未見 holdout（eval 用 make_strip）。
  目的：驗證「餵現場外觀圖 → M28 是否回來、他模是否不退」。
Run: python _build_v91_m28field.py
"""
from __future__ import annotations
import sys, shutil, glob, os, random
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

V9_M  = REPO / "data_v9"  / "mohao"
V91_M = REPO / "data_v91" / "mohao"
HOLD  = REPO / "data_v91" / "_m28_field_holdout"    # raw frames，不進訓練
FIELD_DIRS = [
    r"D:\OCR_demo\output\2026-07-07\M28\01",
    r"D:\OCR_demo\output\2026-07-07\M28\02",
]
FIELD_MISMATCH = r"D:\OCR_demo\output\2026-07-07\_MISMATCH"  # 取 exp_M28*

def roi_save(src, out_dir: Path, prefix: str) -> bool:
    img = imread_unicode(src)
    if img is None: return False
    c = find_circle(img)
    if c is None: return False
    cx, cy, r = c
    roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    ok, buf = cv2.imencode(".jpg", roi, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok: return False
    out_dir.mkdir(parents=True, exist_ok=True)
    buf.tofile(str(out_dir / f"{prefix}{Path(src).stem}__r{r}_1.jpg"))
    return True

def main():
    rng = random.Random(0)
    # 1) 複製 data_v9/mohao → data_v91/mohao
    if V91_M.exists(): print(f"清除舊 {V91_M}"); shutil.rmtree(V91_M)
    print("[1] 複製 data_v9/mohao 全 20 類 ...")
    for split in ("train","val"):
        for cd in sorted((V9_M/split).iterdir()):
            if not cd.is_dir(): continue
            dst = V91_M/split/cd.name; dst.mkdir(parents=True, exist_ok=True)
            for f in cd.glob("*.jpg"): shutil.copy2(f, dst/f.name)

    # 2) 收現場 M28 raw
    field = []
    for d in FIELD_DIRS: field += glob.glob(os.path.join(d, "*.jpg"))
    field += glob.glob(os.path.join(FIELD_MISMATCH, "exp_M28*.jpg"))
    field = sorted(set(field)); rng.shuffle(field)
    ntr = int(len(field)*0.7)
    train_f, hold_f = field[:ntr], field[ntr:]
    print(f"[2] 現場 M28 共 {len(field)} 張 → train {len(train_f)} / holdout {len(hold_f)}")

    # 3) 70% ROI 進 train/M28
    ok = bad = 0
    for f in train_f:
        if roi_save(f, V91_M/"train"/"M28", prefix="fld_"): ok += 1
        else: bad += 1
    print(f"    train/M28 加入現場 ROI: {ok}（失敗 {bad}）")

    # 4) 30% raw 存 holdout（eval 用 make_strip）
    if HOLD.exists(): shutil.rmtree(HOLD)
    HOLD.mkdir(parents=True, exist_ok=True)
    for f in hold_f: shutil.copy2(f, HOLD/os.path.basename(f))
    print(f"    holdout raw 存: {len(hold_f)} -> {HOLD}")

    # 5) 統計 M28
    ntr_m28 = len(list((V91_M/"train"/"M28").glob("*.jpg")))
    print(f"\n[5] M28 train: 原 {len(list((V9_M/'train'/'M28').glob('*.jpg')))} + 現場 {ok} = {ntr_m28}")
    print(f"OUT -> {V91_M}")

if __name__ == "__main__":
    main()
