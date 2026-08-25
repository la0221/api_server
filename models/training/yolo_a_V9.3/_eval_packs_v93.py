# -*- coding: utf-8 -*-
"""V9.3 穴號頭 在 M28第一包 / 第二包（7/8 現場料）上的離線評估。

推論比照部署：環狀 warpPolar R_INNER=0.6 + 2-pass（0° / 90° 取最高信心）。
GT = 資料夾名（穴號）。檔名前綴 exp_ 只代表「線上舊模型判錯」，GT 仍是資料夾。

★ 洩漏標記：逐張用 (timestamp, seq) 比對 data_v93/xuehao {train,val}，
  標 in_train，最後分別報「全體」與「held-out only」兩組數字。

Run: & lens-gpu python _eval_packs_v93.py --device 0
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6.7"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa
from v67_dataset import R_INNER  # noqa

PAD = 255
W = HERE / "runs_v93" / "xuehao" / "weights" / "best.pt"
STABLE = Path("D:/模號穴號-穩定圖片區/M28")
DV93 = REPO / "data_v93" / "xuehao"
KEY = re.compile(r"(\d{12})_(\d+?)(?:__r\d+_\d+)?\.jpg$")


def ann(roi, off, size=640, ri=R_INNER):
    h, w = roi.shape[:2]
    cx, cy = w // 2, h // 2
    if off:
        roi = cv2.warpAffine(roi, cv2.getRotationMatrix2D((cx, cy), off, 1.0),
                             (w, h), flags=cv2.INTER_LINEAR, borderValue=(PAD,) * 3)
    r = min(cx, cy)
    C = 2 * math.pi * r
    pol = cv2.warpPolar(roi, (int(r), int(C)), (cx, cy), r,
                        cv2.INTER_LINEAR + cv2.WARP_POLAR_LINEAR)[:, int(ri * r):]
    return white_pad_square(cv2.transpose(cv2.flip(pol, 1)), size)


def roi_of(f):
    img = imread_unicode(f)
    if img is None:
        return None
    c = find_circle(img)
    if c is None:
        return None
    cx, cy, r = c
    return white_pad_square(img[max(0, cy - r):cy + r, max(0, cx - r):cx + r], target=2 * r)


def p2(m, strips, dev):
    best = ("?", -1.0)
    for s in strips:
        r = m.predict(s, imgsz=640, verbose=False, device=dev)[0]
        p = r.probs.data.cpu().numpy()
        i = int(np.argmax(p))
        if float(p[i]) > best[1]:
            best = (m.names[i], float(p[i]))
    return best


def train_keys():
    ks = set()
    for split in ("train", "val"):
        d = DV93 / split
        if not d.exists():
            continue
        for f in d.glob("*/*.jpg"):
            m = KEY.search(f.name)
            if m:
                ks.add((m.group(1), int(m.group(2))))
    return ks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--packs", default="M28第一包,M28第二包")
    args = ap.parse_args()
    from ultralytics import YOLO
    m = YOLO(str(W))
    tk = train_keys()
    print(f"[v9.3 xuehao] {W}")
    print(f"  data_v93 鍵數={len(tk)}   classes={len(m.names)}")

    result = {}
    for pk in args.packs.split(","):
        base = STABLE / pk / "M28"
        if not base.exists():
            print(f"!! {pk} 不存在"); continue
        cavs = sorted([d.name for d in base.iterdir() if d.is_dir() and d.name.isdigit()])
        print(f"\n######## {pk}  穴號={cavs} ########")
        per = {}
        for cav in cavs:
            files = sorted((base / cav).glob("*.jpg"))
            ok = n = 0
            ok_h = n_h = 0          # held-out only
            wrong = Counter(); hough = 0
            for f in files:
                k = KEY.search(f.name)
                key = (k.group(1), int(k.group(2))) if k else None
                in_tr = key in tk if key else False
                roi = roi_of(f)
                if roi is None:
                    hough += 1
                    continue
                pred, conf = p2(m, [ann(roi, 0), ann(roi, 90)], args.device)
                n += 1
                good = (pred == cav)
                if good:
                    ok += 1
                else:
                    wrong[pred] += 1
                if not in_tr:
                    n_h += 1
                    ok_h += 1 if good else 0
            per[cav] = {"n": n, "ok": ok, "acc": ok / max(1, n),
                        "n_held": n_h, "ok_held": ok_h,
                        "acc_held": ok_h / max(1, n_h),
                        "wrong": dict(wrong.most_common()), "hough_fail": hough}
            w = ", ".join(f"{a}:{b}" for a, b in wrong.most_common(3))
            print(f"  穴{cav}: {ok}/{n} = {ok/max(1,n)*100:6.2f}%   "
                  f"held-out {ok_h}/{n_h} = {ok_h/max(1,n_h)*100:6.2f}%   {w}")
        T = sum(v["n"] for v in per.values()); O = sum(v["ok"] for v in per.values())
        TH = sum(v["n_held"] for v in per.values()); OH = sum(v["ok_held"] for v in per.values())
        print(f"  ── 全包 {O}/{T} = {O/max(1,T)*100:.2f}%    "
              f"held-out {OH}/{TH} = {OH/max(1,TH)*100:.2f}%")
        result[pk] = {"per_cav": per, "total": {"n": T, "ok": O},
                      "held": {"n": TH, "ok": OH}}

    out = HERE / "_eval_packs_v93_result.json"
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n✔ wrote {out}")


if __name__ == "__main__":
    main()
