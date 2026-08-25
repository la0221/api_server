"""v2 crop cache — 用訓好的 YOLOv8n detector 而非密度規則。

每張 strip → detector 找兩個 box → 分別 crop 200×80 → 存到
  data_v671_crops_v2/{split}/{label}/{stem}_{m|x}.png

label 由 detector class 決定（mohao/xuehao），不再靠密度判別。
Mohao label 從資料夾名，xuehao label 從檔名 regex。

用法：
  python -s _build_crop_cache_v2.py --workers 4
"""
from __future__ import annotations

import argparse, re, sys, time
from pathlib import Path
import cv2
import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode, crop_band

REPO = Path(__file__).resolve().parents[2]
SRC_STRIPS = REPO / "data_v671_strips" / "mohao"
DST = REPO / "data_v671_crops_v2"
DETECTOR = Path(__file__).resolve().parent / "runs" / "detector" / "weights" / "best.pt"
CROP_W = 200
HALF_W = CROP_W // 2  # 100
XUEHAO_RE = re.compile(r"(?:exp_)?M\d+[-_](\d{2})_")


def parse_xuehao(fname: str) -> str | None:
    m = XUEHAO_RE.search(fname)
    return m.group(1) if m else None


def wrap_crop(band: np.ndarray, cx: int) -> np.ndarray:
    """80×640 band → 環狀 crop [cx-100, cx+100] → 80×200."""
    W = band.shape[1]
    idx = np.arange(cx - HALF_W, cx + HALF_W) % W
    return band[:, idx]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--conf", type=float, default=0.25)
    args = ap.parse_args()

    from ultralytics import YOLO
    model = YOLO(str(DETECTOR))
    print(f"[detector] loaded {DETECTOR}")

    tasks: list[tuple[Path, str, str]] = []
    for split in ("train", "val"):
        for cls_dir in sorted((SRC_STRIPS / split).iterdir()):
            if not cls_dir.is_dir() or cls_dir.name == "NG":
                continue
            mohao = cls_dir.name
            for p in cls_dir.iterdir():
                if p.suffix.lower() == ".png":
                    tasks.append((p, split, mohao))
    print(f"[build] total tasks={len(tasks)}")

    stats = {"ok": 0, "no_xuehao": 0, "no_detection": 0, "wrong_count": 0}
    t0 = time.time()

    # batched inference
    for start in range(0, len(tasks), args.batch):
        batch = tasks[start:start + args.batch]
        imgs = [imread_unicode(p) for p, _, _ in batch]
        results = model.predict(imgs, conf=args.conf, verbose=False, device=0)
        for (src, split, mohao), r, img in zip(batch, results, imgs):
            xuehao = parse_xuehao(src.stem)
            if xuehao is None:
                stats["no_xuehao"] += 1
                continue
            boxes = r.boxes
            if boxes is None or len(boxes) == 0:
                stats["no_detection"] += 1
                continue
            # 分別找 mohao(cls=0) 和 xuehao(cls=1) 最高信心的框
            cls_arr = boxes.cls.cpu().numpy().astype(int)
            conf_arr = boxes.conf.cpu().numpy()
            xy_arr = boxes.xywh.cpu().numpy()  # xywh in pixels
            m_idx = np.where(cls_arr == 0)[0]
            x_idx = np.where(cls_arr == 1)[0]
            if len(m_idx) == 0 or len(x_idx) == 0:
                stats["wrong_count"] += 1
                continue
            m_best = m_idx[np.argmax(conf_arr[m_idx])]
            x_best = x_idx[np.argmax(conf_arr[x_idx])]
            m_cx = int(round(xy_arr[m_best][0]))
            x_cx = int(round(xy_arr[x_best][0]))
            band = crop_band(img)
            for label, cx, tag in [(mohao, m_cx, "m"), (xuehao, x_cx, "x")]:
                dst = DST / split / label / (src.stem + f"_{tag}.png")
                dst.parent.mkdir(parents=True, exist_ok=True)
                crop = wrap_crop(band, cx)
                ok, buf = cv2.imencode(".png", crop, [cv2.IMWRITE_PNG_COMPRESSION, 3])
                if ok:
                    buf.tofile(str(dst))
            stats["ok"] += 1
        if (start // args.batch) % 20 == 0 or start + args.batch >= len(tasks):
            done = min(start + args.batch, len(tasks))
            rate = done / max(time.time() - t0, 0.1)
            print(f"  [{done}/{len(tasks)}] ok={stats['ok']} "
                  f"no_det={stats['no_detection']} wrong_cnt={stats['wrong_count']} "
                  f"rate={rate:.1f}/s")

    print(f"\n[done] {time.time()-t0:.1f}s; stats={stats}")


if __name__ == "__main__":
    main()
