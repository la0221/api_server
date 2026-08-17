# -*- coding: utf-8 -*-
r"""
⑤ PaddleOCR 開源對照試——A 段：用「與 CRNN 完全相同的前處理」產出 200×80 文字帶 crops。

- 前處理程式碼 **唯讀 import** 驗證區（D:\OCR_demo\models\crnn）的訓練當下那份（find_circle/
  annulus_polar/crop_band），detector 也用同一顆 best.pt（conf=0.10）——確保對照組吃到的
  輸入與我們 CRNN 一模一樣，比的是「辨識器」而不是前處理。
- 輸出：crops\<模號>_<穴號>\<原檔名>_m.png / _x.png + manifest.jsonl（truth 對照表）。
- 在系統 python 跑（torch/ultralytics 已在）；B 段（rapidocr）在獨立 venv 跑，互不相干。

用法：python step1_make_crops.py <資料集根（根\穴號子夾）> <模號正解> [--limit N]
"""
import argparse
import json
import sys

sys.dont_write_bytecode = True   # 四區規則：驗證區唯讀——import 也不准留 __pycache__ 副作用

from pathlib import Path

import numpy as np

CRNN_DIR = Path(r"D:\OCR_demo\models\crnn")
sys.path.insert(0, str(CRNN_DIR))
from crnn_dataset import annulus_polar, crop_band, imread_unicode  # noqa: E402
from v6_preprocess import find_circle, white_pad_square  # noqa: E402

from ultralytics import YOLO  # noqa: E402

DETECTOR_W = CRNN_DIR / "runs" / "detector" / "weights" / "best.pt"
DET_CONF = 0.10          # 策略正典：碎片/淺印框信心偏低，0.5 會兩頭堵
HALF_W = 100             # crop 半寬 → 200×80，不可改
CLS_MOHAO, CLS_XUEHAO = 0, 1


def to_strip(img, size=640):
    circ = find_circle(img)
    if circ is None:
        return white_pad_square(img, size), False
    cx, cy, r = circ
    x0, y0 = max(0, cx - r), max(0, cy - r)
    x1, y1 = min(img.shape[1], cx + r), min(img.shape[0], cy + r)
    roi = white_pad_square(img[y0:y1, x0:x1], target=2 * r)
    return annulus_polar(roi, do_rotate=False, size=size), True


def pick_center(boxes, cls_id):
    idxs = np.where(boxes["cls"] == cls_id)[0]
    if len(idxs) == 0:
        return None
    best = idxs[np.argmax(boxes["conf"][idxs])]
    return int(round(boxes["xywh"][best][0]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root", help="資料集根（根\\穴號子夾\\*.jpg）")
    ap.add_argument("mohao", help="模號正解（如 M101）")
    ap.add_argument("--limit", type=int, default=0, help="每穴最多張數（0=全部）")
    args = ap.parse_args()

    import cv2

    det = YOLO(str(DETECTOR_W))
    out_dir = Path(__file__).parent / "crops"
    out_dir.mkdir(exist_ok=True)
    manifest = open(Path(__file__).parent / "manifest.jsonl", "a", encoding="utf-8")

    root = Path(args.root)
    total = ok = miss_circle = miss_det = 0
    for sub in sorted(p for p in root.iterdir() if p.is_dir()):
        xuehao_truth = sub.name
        files = sorted(sub.glob("*.jpg")) + sorted(sub.glob("*.png"))
        if args.limit > 0:
            files = files[: args.limit]
        for f in files:
            total += 1
            img = imread_unicode(str(f))
            if img is None:
                continue
            strip, hough = to_strip(img)
            if not hough:
                miss_circle += 1
                continue
            r = det.predict(strip, verbose=False, conf=DET_CONF)[0]
            if r.boxes is None or len(r.boxes) == 0:
                miss_det += 1
                continue
            boxes = {
                "cls": r.boxes.cls.cpu().numpy().astype(int),
                "conf": r.boxes.conf.cpu().numpy(),
                "xywh": r.boxes.xywh.cpu().numpy(),
            }
            m_cx = pick_center(boxes, CLS_MOHAO)
            x_cx = pick_center(boxes, CLS_XUEHAO)
            if m_cx is None or x_cx is None:
                miss_det += 1
                continue

            band = crop_band(strip)
            w = band.shape[1]
            m_crop = band[:, np.arange(m_cx - HALF_W, m_cx + HALF_W) % w]
            x_crop = band[:, np.arange(x_cx - HALF_W, x_cx + HALF_W) % w]

            grp = out_dir / f"{args.mohao}_{xuehao_truth}"
            grp.mkdir(exist_ok=True)
            m_path = grp / f"{f.stem}_m.png"
            x_path = grp / f"{f.stem}_x.png"
            cv2.imwrite(str(m_path), m_crop)
            cv2.imwrite(str(x_path), x_crop)
            manifest.write(json.dumps({
                "mohao_truth": args.mohao, "xuehao_truth": xuehao_truth,
                "m_crop": str(m_path), "x_crop": str(x_path), "src": str(f),
            }, ensure_ascii=False) + "\n")
            ok += 1

    manifest.close()
    print(f"[OK] 總 {total} 張：成功 {ok}、Hough 失敗 {miss_circle}、detector 失敗 {miss_det}")
    print(f"     crops → {out_dir}")


if __name__ == "__main__":
    main()
