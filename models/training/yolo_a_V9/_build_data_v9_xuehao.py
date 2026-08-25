# -*- coding: utf-8 -*-
"""V9 穴號 全量重訓資料集（A 軸；不 warm-start）。
  base   = data_v671/xuehao 全穴號 01–18（實體複製，跨模池化）
  M17 加 = data3/M17/<穴號>（721 原圖）ROI 化 → xuehao/train/<該穴號>（prefix d3M17_）
  data4/M17 保留不進訓練（未見探針，_eval_v9_xuehao.py 用）
Run: python _build_data_v9_xuehao.py
"""
from __future__ import annotations
import sys, shutil, os
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

V671_X = REPO / "data_v671" / "xuehao"
V9_X    = REPO / "data_v9" / "xuehao"
D3_M17  = REPO / "data3" / "M17"     # 下有 01..18 穴號子夾

def roi_save(src_jpg: Path, out_dir: Path, prefix: str) -> bool:
    img = imread_unicode(src_jpg)
    if img is None: return False
    circ = find_circle(img)
    if circ is None: return False
    cx, cy, r = circ
    roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    ok, buf = cv2.imencode(".jpg", roi, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok: return False
    out_dir.mkdir(parents=True, exist_ok=True)
    buf.tofile(str(out_dir / f"{prefix}{src_jpg.stem}__r{r}_1.jpg"))
    return True

def main():
    if V9_X.exists():
        print(f"清除舊 {V9_X}"); shutil.rmtree(V9_X)
    cavs = sorted([d.name for d in (V671_X/"train").iterdir() if d.is_dir()])
    print(f"[1] 複製 {len(cavs)} 個穴號 base（data_v671/xuehao）...")
    for split in ("train","val"):
        for c in cavs:
            src = V671_X/split/c; dst = V9_X/split/c
            dst.mkdir(parents=True, exist_ok=True)
            for f in src.glob("*.jpg"): shutil.copy2(f, dst/f.name)

    # 2) data3/M17/<穴號> ROI 化併入對應穴號 train
    print(f"[2] data3/M17 依穴號 ROI 化併入 xuehao/train ...")
    ok = bad = 0; per = {}
    for cav_dir in sorted([d for d in D3_M17.iterdir() if d.is_dir()]):
        cav = cav_dir.name                      # 穴號真值 = 資料夾名
        if cav not in cavs:
            print(f"    ! data3/M17/{cav} 不在 xuehao 類別中，跳過"); continue
        cnt = 0
        for f in sorted(cav_dir.rglob("*.jpg")):
            if roi_save(f, V9_X/"train"/cav, prefix="d3M17_"): ok += 1; cnt += 1
            else: bad += 1
        per[cav] = cnt
    print(f"    data3/M17 ROI 成功 {ok}，失敗 {bad}；各穴號加入 {per}")

    # 3) 統計
    print("\n[3] data_v9/xuehao 各穴號張數：")
    tot_tr = tot_va = 0
    for c in cavs:
        ntr = len(list((V9_X/"train"/c).glob("*.jpg")))
        nva = len(list((V9_X/"val"/c).glob("*.jpg")))
        tot_tr += ntr; tot_va += nva
        add = f"  <== +data3 {per.get(c,0)}" if per.get(c) else ""
        print(f"    穴{c}  train={ntr:5d}  val={nva:4d}{add}")
    print(f"    ----- total train={tot_tr}  val={tot_va}")
    print(f"\nOUT -> {V9_X}")

if __name__ == "__main__":
    main()
