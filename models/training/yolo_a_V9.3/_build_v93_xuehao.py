# -*- coding: utf-8 -*-
"""V9.3 穴號 = data_v92n/xuehao + M28-04「新字體」現場圖(凸框粗體那顆模具)進穴號04。

背景:v9.2n 已修好 M28-04 舊字體(04→06 57.5→100%),但新一批 `錯誤M28-04/M28_2`
     是**新的刻印字體**(字在凸起方框上、粗體) → v9.2 沒見過 → 42% 誤判(04→06)。
     = 記憶預言的「舊模號新外觀→必須餵該機台圖」打地鼠,增強救不了。

M28_2 316 張 → 250 train / 30 val / 36 holdout(holdout 不進訓練,驗證用)。
比照 _build_v92_xuehao.py:實體夾複製 + roi_save(Hough→2r ROI→__r{r}_1)。

Run: python _build_v93_xuehao.py
"""
from __future__ import annotations

import random
import shutil
import sys
from pathlib import Path

import cv2

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

SRC = REPO / "data_v92n" / "xuehao"          # v9.2n 基底(19類含NG)
DST = REPO / "data_v93" / "xuehao"
NEW = REPO / "錯誤M28-04" / "M28_2"           # 新字體那顆(316張,全 M28-04)
HOLD = HERE / "m28_newfont_holdout"
CAV = "04"
SEED = 0


def roi_save(src, out_dir: Path, prefix: str) -> bool:
    img = imread_unicode(src)
    if img is None:
        return False
    c = find_circle(img)
    if c is None:
        return False
    cx, cy, r = c
    roi = white_pad_square(img[max(0, cy - r):cy + r, max(0, cx - r):cx + r], target=2 * r)
    ok, buf = cv2.imencode(".jpg", roi, [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok:
        return False
    out_dir.mkdir(parents=True, exist_ok=True)
    buf.tofile(str(out_dir / f"{prefix}{Path(src).stem}__r{r}_1.jpg"))
    return True


def main():
    if DST.exists():
        print(f"清除舊 {DST}"); shutil.rmtree(DST)
    print(f"[1] 複製 data_v92n/xuehao → data_v93/xuehao ...")
    shutil.copytree(SRC, DST, ignore=shutil.ignore_patterns("*.cache"))
    cls = sorted(d.name for d in (DST / "train").iterdir() if d.is_dir())
    print(f"  {len(cls)} 類: {' '.join(cls)}")

    # 新字體 316 → 250 train / 30 val / 36 holdout
    rng = random.Random(SEED)
    news = sorted(f for f in NEW.rglob("*.jpg")
                  if not any(t in f.name for t in ("_DIAG", "_c_", "_ann")))
    rng.shuffle(news)
    tr, va, ho = news[:250], news[250:280], news[280:]
    HOLD.mkdir(parents=True, exist_ok=True)
    for f in HOLD.glob("*.jpg"):
        f.unlink()
    nt = sum(roi_save(f, DST / "train" / CAV, "m28nf_") for f in tr)
    nv = sum(roi_save(f, DST / "val" / CAV, "m28nf_") for f in va)
    for f in ho:
        shutil.copy2(f, HOLD / f.name)
    print(f"\n[2] 新字體 M28_2 {len(news)} → train+{nt} / val+{nv} / holdout {len(ho)}")
    print(f"  穴號{CAV} train: {len(list((DST/'train'/CAV).glob('*.jpg')))}"
          f"  val: {len(list((DST/'val'/CAV).glob('*.jpg')))}")
    tot_t = sum(len(list((DST/'train'/c).glob('*.jpg'))) for c in cls)
    tot_v = sum(len(list((DST/'val'/c).glob('*.jpg'))) for c in cls)
    print(f"  data_v93/xuehao train={tot_t} val={tot_v}")
    print(f"\n  ⚠ 類別平衡檢查(04 是否過大):")
    for c in cls:
        n = len(list((DST/'train'/c).glob('*.jpg')))
        flag = "  ← 最大" if c == CAV else ""
        print(f"    {c}: {n}{flag}")


if __name__ == "__main__":
    main()
