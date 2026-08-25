"""通用字元 bbox 手標 — 依 (mold_class, char, n) 抽 crop、標左右邊界。

用法：
  python -s _label_char_multi.py --mold M17 --char 7 --n 30
  python -s _label_char_multi.py --mold M50 --char 0 --n 30
  python -s _label_char_multi.py --mold M23 --char 3 --n 30

存 JSON: labels/char_bbox_{char}.json （同字元跨 mold 合併）
"""
from __future__ import annotations
import argparse, json, sys, random
from pathlib import Path
import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode

REPO = Path(__file__).resolve().parents[2]
LABELS_DIR = Path(__file__).resolve().parent / "labels"
LABELS_DIR.mkdir(parents=True, exist_ok=True)
ZOOM = 3
CROP_H, CROP_W = 80, 200
DH, DW = CROP_H * ZOOM, CROP_W * ZOOM

# 字元在各 mold 的位置索引 (0-based)
CHAR_POSITION = {
    "M17": {"M": 0, "1": 1, "7": 2},
    "M50": {"M": 0, "5": 1, "0": 2},
    "M23": {"M": 0, "2": 1, "3": 2},
    # 需要時可擴充其他 mold
}


def load_progress(char):
    p = LABELS_DIR / f"char_bbox_{char}.json"
    if p.exists():
        return {r["path"]: r for r in json.loads(p.read_text(encoding="utf-8"))}, p
    return {}, p


def save_progress(records, path):
    path.write_text(json.dumps(list(records.values()), indent=2, ensure_ascii=False),
                    encoding="utf-8")


def render(crop, clicks, char):
    disp = cv2.resize(crop, (DW, DH), interpolation=cv2.INTER_NEAREST)
    tags = ["L (green)", "R (blue)"]
    colors = [(0, 255, 0), (255, 0, 0)]
    for i, x in enumerate(clicks):
        dx = x * ZOOM
        cv2.line(disp, (dx, 0), (dx, DH - 1), colors[i], 2)
        cv2.putText(disp, tags[i], (max(2, dx - 40), 20),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, colors[i], 2)
    banner = np.ones((30, DW, 3), dtype=np.uint8) * 245
    txt = f"CHAR = '{char}'    click {len(clicks)}/2 (L green, R blue)  Enter=save  Esc=skip  u=undo  q=quit"
    cv2.putText(banner, txt, (10, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, 0, 1)
    return np.vstack([banner, disp])


def label_loop(pool, records, char, save_path):
    idx = 0
    while idx < len(pool):
        p = pool[idx]
        key = str(p)
        if key in records and "left_x" in records[key]:
            idx += 1; continue
        crop = imread_unicode(p)
        clicks = []

        def cb(event, x, y, flags, param):
            if event == cv2.EVENT_LBUTTONDOWN and len(clicks) < 2:
                cx = min(CROP_W - 1, max(0, x // ZOOM))
                clicks.append(cx)
                cv2.imshow("char", render(crop, clicks, char))

        cv2.namedWindow("char", cv2.WINDOW_AUTOSIZE)
        cv2.setMouseCallback("char", cb)
        cv2.setWindowTitle("char", f"[{idx+1}/{len(pool)}] char='{char}' {p.name}")
        cv2.imshow("char", render(crop, clicks, char))

        act = None
        while act is None:
            k = cv2.waitKey(20) & 0xFF
            if k == 13 and len(clicks) == 2:
                l, r = min(clicks), max(clicks)
                if r - l < 5:
                    print("  too narrow, retry")
                    clicks.clear()
                    cv2.imshow("char", render(crop, clicks, char))
                    continue
                records[key] = {"path": key, "char": char,
                                 "left_x": l, "right_x": r}
                save_progress(records, save_path)
                act = "next"
            elif k == 27:
                act = "next"
            elif k == ord("u") and clicks:
                clicks.pop()
                cv2.imshow("char", render(crop, clicks, char))
            elif k == ord("q"):
                cv2.destroyAllWindows()
                print(f"[quit] {len(records)} labeled → {save_path}")
                return
        idx += 1
    cv2.destroyAllWindows()
    print(f"[done] {len(records)} labeled → {save_path}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mold", required=True, help="e.g. M17, M50, M23")
    ap.add_argument("--char", required=True, help="e.g. 7, 0, 3")
    ap.add_argument("--n", type=int, default=30)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    if args.mold not in CHAR_POSITION or args.char not in CHAR_POSITION[args.mold]:
        raise ValueError(f"{args.mold}/{args.char} not registered. add to CHAR_POSITION.")

    mold_dir = REPO / "data_v671_crops_v2" / "train" / args.mold
    all_crops = sorted(mold_dir.glob("*_m.png"))
    if not all_crops:
        raise FileNotFoundError(mold_dir)
    rng = random.Random(args.seed)
    rng.shuffle(all_crops)
    pool = all_crops[:args.n]

    records, save_path = load_progress(args.char)
    print(f"[pool] {len(pool)} {args.mold} crops  ({len(records)} already labeled for char='{args.char}')")
    label_loop(pool, records, args.char, save_path)


if __name__ == "__main__":
    main()
