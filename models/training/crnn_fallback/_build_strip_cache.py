"""一次性把 data_v671/mohao raw → 640×640 polar strip PNG 快取。

之後 CRNN dataset 直接讀 strip PNG，跳過 Hough + warpPolar，訓練從 4.5h → 15min。

輸出結構：
  data_v671_strips/mohao/{train,val}/{mold}/{filename}.png

Hough fail 的檔案：不寫入快取（避免污染訓練），寫到 skipped.txt。

用法：
  python -s _build_strip_cache.py [--workers 8]
"""
from __future__ import annotations

import argparse
import multiprocessing as mp
import sys
import time
from pathlib import Path

import cv2

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from crnn_dataset import annulus_polar, imread_unicode, find_circle, white_pad_square

REPO = HERE.parents[1]
SRC = REPO / "data_v671" / "mohao"
DST = REPO / "data_v671_strips" / "mohao"
SIZE = 640


def process_one(args: tuple[Path, Path]) -> tuple[str, str]:
    """讀 raw 圖 → strip → 存 PNG。回傳 (status, msg)。"""
    src, dst = args
    try:
        img = imread_unicode(src)
        circ = find_circle(img)
        if circ is None:
            return ("hough_fail", str(src))
        cx, cy, r = circ
        x0, y0 = max(0, cx - r), max(0, cy - r)
        x1, y1 = min(img.shape[1], cx + r), min(img.shape[0], cy + r)
        roi = white_pad_square(img[y0:y1, x0:x1], target=2 * r)
        strip = annulus_polar(roi, do_rotate=False, size=SIZE)
        dst.parent.mkdir(parents=True, exist_ok=True)
        # imencode 再 tofile 才能寫 CJK 路徑
        ok, buf = cv2.imencode(".png", strip, [cv2.IMWRITE_PNG_COMPRESSION, 3])
        if not ok:
            return ("encode_fail", str(src))
        buf.tofile(str(dst))
        return ("ok", "")
    except Exception as e:
        return ("exception", f"{src}: {e!r}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workers", type=int, default=8)
    args = ap.parse_args()

    print(f"[build] SRC={SRC}\n[build] DST={DST}")
    tasks: list[tuple[Path, Path]] = []
    for split in ("train", "val"):
        for cls_dir in sorted((SRC / split).iterdir()):
            if not cls_dir.is_dir():
                continue
            for src in cls_dir.iterdir():
                if src.suffix.lower() not in {".jpg", ".jpeg", ".png"}:
                    continue
                dst = DST / split / cls_dir.name / (src.stem + ".png")
                tasks.append((src, dst))

    print(f"[build] total tasks={len(tasks)} workers={args.workers}")
    t0 = time.time()
    stats: dict[str, int] = {"ok": 0, "hough_fail": 0, "encode_fail": 0, "exception": 0}
    skipped: list[str] = []

    with mp.Pool(args.workers) as pool:
        for i, (status, msg) in enumerate(pool.imap_unordered(process_one, tasks, chunksize=16), 1):
            stats[status] = stats.get(status, 0) + 1
            if status != "ok":
                skipped.append(f"{status}\t{msg}")
            if i % 500 == 0 or i == len(tasks):
                elapsed = time.time() - t0
                rate = i / max(elapsed, 0.1)
                eta = (len(tasks) - i) / max(rate, 0.1)
                print(f"  [{i}/{len(tasks)}] ok={stats['ok']} "
                      f"hough_fail={stats['hough_fail']} "
                      f"exc={stats['exception']} "
                      f"rate={rate:.1f}/s eta={eta:.0f}s")

    dt = time.time() - t0
    print(f"\n[done] {dt:.1f}s total; stats={stats}")

    DST.mkdir(parents=True, exist_ok=True)
    with open(DST / "skipped.txt", "w", encoding="utf-8") as f:
        f.write("\n".join(skipped))
    if skipped:
        print(f"[warn] {len(skipped)} skipped → {DST / 'skipped.txt'}")


if __name__ == "__main__":
    main()
