# -*- coding: utf-8 -*-
"""V9.2 穴號 = data_v671/xuehao + data3/M17(依穴號) + M28-04 現場圖(進穴號04)。
  沿用 mohao 的 200 holdout（data_v92/_m28_04_holdout）→ 這裡排除，不進訓練。
Run: python _build_v92_xuehao.py
"""
from __future__ import annotations
import sys, shutil, glob, os
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent; REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

V671_X = REPO / "data_v671" / "xuehao"
V92_X   = REPO / "data_v92" / "xuehao"
D3_M17  = REPO / "data3" / "M17"
HOLD    = REPO / "data_v92" / "_m28_04_holdout"     # mohao 已存的 200，排除
SU = REPO / "尚未收完全" / "M28"
CLEAN_04 = [SU/"M28第一包"/"M28"/"04", SU/"M28第三包"/"M28"/"04"]
MM_DIRS  = [SU/"M28第一包"/"_MISMATCH", SU/"M28第三包"/"_MISMATCH"]

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
    if V92_X.exists(): print("清除舊", V92_X); shutil.rmtree(V92_X)
    cavs = sorted([d.name for d in (V671_X/"train").iterdir() if d.is_dir()])
    print(f"[1] 複製 data_v671/xuehao {len(cavs)} 穴號 ...")
    for split in ("train","val"):
        for c in cavs:
            src = V671_X/split/c; dst = V92_X/split/c; dst.mkdir(parents=True, exist_ok=True)
            for f in src.glob("*.jpg"): shutil.copy2(f, dst/f.name)

    # 2) data3/M17 依穴號
    ok3 = 0
    for cav_dir in sorted([d for d in D3_M17.iterdir() if d.is_dir()]):
        cav = cav_dir.name
        if cav not in cavs: continue
        for f in sorted(cav_dir.rglob("*.jpg")): ok3 += roi_save(f, V92_X/"train"/cav, "d3M17_")
    print(f"[2] data3/M17 進穴號 train: {ok3}")

    # 3) M28-04 → 穴號 04（排除 holdout）
    hold_names = {os.path.basename(p) for p in glob.glob(str(HOLD/"*.jpg"))}
    clean = []
    for d in CLEAN_04: clean += glob.glob(str(d/"*.jpg"))
    clean = [p for p in sorted(set(clean)) if os.path.basename(p) not in hold_names]
    okc = 0
    for f in clean: okc += roi_save(f, V92_X/"train"/"04", "m28fld04_")
    hard = []
    for d in MM_DIRS: hard += glob.glob(str(d/"exp_M28-04_*.jpg"))
    okh = 0
    for f in sorted(set(hard)): okh += roi_save(f, V92_X/"train"/"04", "m28hard04_")
    print(f"[3] M28-04 進穴號04: clean {okc} + hard {okh}（holdout {len(hold_names)} 已排除）")

    n04 = len(list((V92_X/"train"/"04").glob("*.jpg")))
    print(f"\n穴號04 train 總計 = {n04}")
    print("OUT ->", V92_X)

if __name__ == "__main__":
    main()
