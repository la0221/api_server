# -*- coding: utf-8 -*-
"""V9.4 模號資料 = data_v92/mohao 平衡化 + v9combo_errors 難例（對回原圖→ROI→真模號）。

依 2026-07-27 討論（模號比照穴號同一套紀律）：
  - data_v92/mohao 嚴重不平衡：M28=1189 / M60=983 / M17=890（最小 M59=215 的 4-5×）。
    最大宗模號錯 M54→M17 很可能與 M17 被灌大有關（同吸收槽機制）→ 每類砍到 CAP=360。
  - 加強＝v9combo_errors（298 張 v9混搭在前處理區讀錯的），對回穩定圖片區原圖→ROI→真模號。
    模號錯主力：M54→M17/M95、M83→M95/M101/M82。
  - base 用 data_v92（保住進度）；含 NG（20 類）。

★ 訓練用原圖 stem 與穴號共用同一份 `_v94_trained_error_stems.txt`（同 298 張）。

Run: python _build_v94_mohao.py
"""
from __future__ import annotations

import random
import re
import shutil
import sys
from pathlib import Path

import cv2

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa

SRC = REPO / "data_v92" / "mohao"
DST = REPO / "data_v94" / "mohao"
ERR = Path("D:/tmp/v9.3vsrcnn/v9combo_errors")
STABLE = Path("D:/模號穴號-穩定圖片區")
CAP = 360
SEED = 0
STEMS_OUT = HERE / "_v94_trained_error_stems.txt"   # 與穴號共用
KEY = re.compile(r"^gt-(M\d+)-(\d+)__v9-[^_]+-[^_]+__(.+)_p0\.png$")


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
    rng = random.Random(SEED)
    if DST.exists():
        print(f"清除舊 {DST}"); shutil.rmtree(DST)
    print("[1] 複製 data_v92/mohao → data_v94/mohao ...")
    shutil.copytree(SRC, DST, ignore=shutil.ignore_patterns("*.cache"))
    cls = sorted(d.name for d in (DST / "train").iterdir() if d.is_dir())

    print(f"[2] 平衡：每模號 train 砍到 CAP={CAP}(NG 不砍)")
    for c in cls:
        if c == "NG":
            continue
        files = sorted((DST / "train" / c).glob("*.jpg"))
        if len(files) > CAP:
            rng.shuffle(files)
            for f in files[CAP:]:
                f.unlink()

    print("[3] 建原圖索引 ...")
    idx = {}
    for f in STABLE.rglob("*.jpg"):
        idx.setdefault(f.stem, f)
    print(f"  索引 {len(idx)} 張")

    print("[4] 加入 v9combo_errors 難例(進真模號) ...")
    added = {c: 0 for c in cls}
    used = []; miss = 0
    for s in sorted(ERR.rglob("*.png")):
        m = KEY.match(s.name)
        if not m:
            continue
        mold, stem = m.group(1), m.group(3)
        raw = idx.get(stem)
        if raw is None:
            miss += 1; continue
        if mold not in added:
            continue
        if roi_save(raw, DST / "train" / mold, "v9e_"):
            added[mold] += 1; used.append(stem)
    prev = set(STEMS_OUT.read_text(encoding="utf-8").split()) if STEMS_OUT.exists() else set()
    allstems = sorted(prev | set(used))
    STEMS_OUT.write_text("\n".join(allstems), encoding="utf-8")
    print(f"  加入 {sum(added.values())}(對不回 {miss});守門排除 stem 聯集={len(allstems)}")

    print("\n[5] data_v94/mohao/train 最終各類:")
    tot = 0
    for c in cls:
        n = len(list((DST / "train" / c).glob("*.jpg"))); tot += n
        print(f"    {c}: {n}  (+{added.get(c,0)})")
    print(f"  train 合計 {tot}")


if __name__ == "__main__":
    main()
