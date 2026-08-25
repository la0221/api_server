# -*- coding: utf-8 -*-
"""V9.4 穴號資料 = data_v92n/xuehao 平衡化(斷 04 吸收槽) + v9combo_errors 難例。

依 2026-07-27 討論：
  - v9.3 穴號塌成 04 吸收槽,根因＝data_v92n 的 04=1034(3× 其他類)。
  - 修法＝**每類砍到 CAP=360**(04:1034→360, 03:562→360),NG 保留 → 一刀斷成因。
  - 加強＝`D:/tmp/v9.3vsrcnn/v9combo_errors`(v9混搭在前處理區真正讀錯的 298 張難例,
    主力 09→06 / 15→16 等硬數字對)→ 對回穩定圖片區原圖 → roi_save 進各自「真穴號」。
  - 保住進度＝base 用 data_v92n(累積所有現場料);模號不動。

★ 訓練用到的原圖 stem 存 `_v94_trained_error_stems.txt`,守門時排除,避免 train/test 洩漏。

Run: python _build_v94_xuehao.py
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

SRC = REPO / "data_v92n" / "xuehao"
DST = REPO / "data_v94" / "xuehao"
ERR = Path("D:/tmp/v9.3vsrcnn/v9combo_errors")
STABLE = Path("D:/模號穴號-穩定圖片區")
CAP = 360           # 每類 train 上限(斷吸收槽)
SEED = 0
STEMS_OUT = HERE / "_v94_trained_error_stems.txt"

import re
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
    print("[1] 複製 data_v92n/xuehao → data_v94/xuehao ...")
    shutil.copytree(SRC, DST, ignore=shutil.ignore_patterns("*.cache"))
    cls = sorted(d.name for d in (DST / "train").iterdir() if d.is_dir())

    # [2] 每類 train 砍到 CAP(NG 不砍)
    print(f"[2] 平衡：每穴號 train 砍到 CAP={CAP}(NG 不砍)")
    for c in cls:
        if c == "NG":
            continue
        files = sorted((DST / "train" / c).glob("*.jpg"))
        if len(files) > CAP:
            rng.shuffle(files)
            for f in files[CAP:]:
                f.unlink()

    # [3] 建 穩定圖片區 原圖 basename→path 索引(一次)
    print("[3] 建原圖索引 ...")
    idx = {}
    for f in STABLE.rglob("*.jpg"):
        idx.setdefault(f.stem, f)   # 同名取第一個
    print(f"  索引 {len(idx)} 張原圖")

    # [4] v9combo_errors → 原圖 → roi_save 進真穴號
    print("[4] 加入 v9combo_errors 難例 ...")
    added = {c: 0 for c in cls}
    used_stems = []
    miss = 0
    for s in sorted(ERR.rglob("*.png")):
        m = KEY.match(s.name)
        if not m:
            continue
        cav, stem = m.group(2), m.group(3)
        raw = idx.get(stem)
        if raw is None:
            miss += 1
            continue
        if cav not in added:      # 只收 01-18(NG 不從錯誤集加)
            continue
        if roi_save(raw, DST / "train" / cav, "v9e_"):
            added[cav] += 1
            used_stems.append(stem)
    STEMS_OUT.write_text("\n".join(sorted(set(used_stems))), encoding="utf-8")
    print(f"  加入 {sum(added.values())} 張(對不回原圖 {miss});stem 清單→{STEMS_OUT.name}")

    # [5] 最終平衡
    print("\n[5] data_v94/xuehao/train 最終各類:")
    tot = 0
    for c in cls:
        n = len(list((DST / "train" / c).glob("*.jpg")))
        tot += n
        print(f"    {c}: {n}  (+{added.get(c,0)} 難例)")
    print(f"  train 合計 {tot}")


if __name__ == "__main__":
    main()
