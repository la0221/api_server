# -*- coding: utf-8 -*-
"""V9 mohao 全量重訓資料集（A 軸驗證：不 warm-start）。
  base   = data_v671/mohao 全 20 類（實體複製）
  M17 加 = data3/M17（721 原圖）ROI 化 → train/M17（tier1，prefix d3_）
  data4/M17 保留不進訓練（另作未見探針，見 _probe_data4.py）
Run: python _build_data_v9.py
"""
from __future__ import annotations
import sys, shutil
from pathlib import Path
import cv2
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

V671_M = REPO / "data_v671" / "mohao"
V9_M    = REPO / "data_v9" / "mohao"
D3_M17  = REPO / "data3" / "M17"

def roi_save(src_jpg: Path, out_dir: Path, prefix: str) -> bool:
    img = imread_unicode(src_jpg)
    if img is None: return False
    circ = find_circle(img)
    if circ is None: return False
    cx, cy, r = circ
    roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    ok, buf = cv2.imencode(".jpg", roi, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok: return False
    buf.tofile(str(out_dir / f"{prefix}{src_jpg.stem}__r{r}_1.jpg"))
    return True

def main():
    # 1) 實體複製 data_v671/mohao 全類 → data_v9/mohao
    if V9_M.exists():
        print(f"清除舊 {V9_M}"); shutil.rmtree(V9_M)
    classes = sorted([d.name for d in (V671_M/"train").iterdir() if d.is_dir()])
    print(f"[1] 複製 {len(classes)} 類 base（data_v671）...")
    for split in ("train","val"):
        for cls in classes:
            src = V671_M/split/cls; dst = V9_M/split/cls
            dst.mkdir(parents=True, exist_ok=True)
            for f in src.glob("*.jpg"): shutil.copy2(f, dst/f.name)

    base_m17_tr = len(list((V9_M/"train"/"M17").glob("*.jpg")))

    # 2) data3/M17 ROI 化併入 train/M17（tier1）
    print(f"[2] data3/M17 ROI 化併入 train/M17 ...")
    srcs = sorted(D3_M17.rglob("*.jpg"))
    ok = bad = 0
    for f in srcs:
        if roi_save(f, V9_M/"train"/"M17", prefix="d3_"): ok += 1
        else: bad += 1
    print(f"    data3/M17: 來源 {len(srcs)}，ROI 成功 {ok}，失敗(find_circle None) {bad}")

    # 3) 統計
    print("\n[3] data_v9/mohao 各類張數：")
    tot_tr = tot_va = 0
    for cls in classes:
        ntr = len(list((V9_M/"train"/cls).glob("*.jpg")))
        nva = len(list((V9_M/"val"/cls).glob("*.jpg")))
        tot_tr += ntr; tot_va += nva
        mark = "  <== +data3" if cls=="M17" else ""
        print(f"    {cls:5s} train={ntr:5d}  val={nva:4d}{mark}")
    print(f"    ----- total train={tot_tr}  val={tot_va}")
    print(f"    M17 train: {base_m17_tr} (base) + {ok} (data3) = {base_m17_tr+ok}")
    print(f"\nOUT -> {V9_M}")

if __name__ == "__main__":
    main()
