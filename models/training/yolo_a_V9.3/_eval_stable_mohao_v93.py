# -*- coding: utf-8 -*-
"""V9.3 模號 head 在「D:\\模號穴號-穩定圖片區」全量離線驗證。

範圍：只掃 <模號>/<模號_數字>/ 這種資料夾（如 M101/M101_1），遞迴抓底下所有 jpg。
GT   ：★從檔名取真實模號 (?:exp_)?M<digits>-<cav>（不用資料夾名——M101_3 內混有真 M83 圖）。
推論 ：環狀 warpPolar R_INNER=0.6 + 2-pass（0°/90° 取最高信心），比照部署。
權重 ：weights_for('V9.3')[0]（＝v9.2 模號；v9.3 只改穴號）。

輸出（D:\\模號穴號驗證，鏡像原層級）：
  1. 誤判圖：pred≠檔名真值者複製到鏡像路徑，檔名前綴 pred-<誤判模號>__
  2. _準確度統計.md / .json：每個 <模號_數字> 資料夾（含夾內各真模號拆解）+ 每真模號 + 總體
  3. 實驗發現.md：本腳本只寫數據段，結論人工補

Run: & lens-gpu python _eval_stable_mohao_v93.py --device 0
"""
from __future__ import annotations

import argparse
import json
import math
import re
import shutil
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6.7"))
sys.path.insert(0, "D:/OCR_demo")
from v6_preprocess import imread_unicode, find_circle, white_pad_square  # noqa
from v67_dataset import R_INNER  # noqa
from app.config import weights_for  # noqa

PAD = 255
SRC = Path("D:/模號穴號-穩定圖片區")
OUT = Path("D:/模號穴號驗證")
MOLDS = ["M101", "M17", "M28", "M54", "M83"]
BIMG = 16   # 每批圖數（每張 2 strip → 每批最多 32 strip，3050 4GB 安全）
KEY = re.compile(r"(?:exp_)?(M\d+)-(\d+)")   # 檔名真實模號-穴號


def true_mold(fname):
    """從檔名取真實模號（M101_3 內混有真 M83 圖 → 不能用資料夾名）。"""
    m = KEY.search(fname)
    return m.group(1) if m else None


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


def roi_of(img):
    c = find_circle(img)
    if c is None:
        return None
    cx, cy, r = c
    return white_pad_square(img[max(0, cy - r):cy + r, max(0, cx - r):cx + r], target=2 * r)


def sub_dirs(mold):
    """<模號>/ 底下形如 <模號>_<數字> 的資料夾。"""
    base = SRC / mold
    if not base.exists():
        return []
    out = []
    for d in sorted(base.iterdir()):
        if d.is_dir() and d.name.startswith(mold + "_") and d.name[len(mold) + 1:].isdigit():
            out.append(d)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--limit", type=int, default=0, help="每資料夾抽樣上限（0=全量）")
    args = ap.parse_args()
    from ultralytics import YOLO

    W = weights_for("V9.3")[0]
    m = YOLO(str(W))
    dev = args.device
    OUT.mkdir(parents=True, exist_ok=True)
    print(f"[V9.3 模號] {W}")
    print(f"  classes={len(m.names)}  device={dev}  2-pass annulus R_INNER={R_INNER}")
    print(f"  來源={SRC}\n  輸出={OUT}\n", flush=True)

    results = {}     # folder_rel -> {folder_mold,n,ok,hough_fail,parse_fail,acc,by_mold}
    t0 = time.time()

    for mold in MOLDS:
        for fold in sub_dirs(mold):
            files = sorted(fold.rglob("*.jpg"))
            if args.limit:
                files = files[:args.limit]
            rel = fold.relative_to(SRC).as_posix()
            n = ok = hough = pfail = 0
            # by_mold[真模號] = {"n":, "ok":, "conf": Counter(pred)}
            by_mold = defaultdict(lambda: {"n": 0, "ok": 0, "conf": Counter()})
            done = 0
            for bi in range(0, len(files), BIMG):
                batch = files[bi:bi + BIMG]
                strips = []          # flat strip list
                owner = []           # strip -> image idx in batch
                imgs_meta = []       # (file, gt|None, roi_ok)
                for f in batch:
                    gt = true_mold(f.name)
                    if gt is None:
                        imgs_meta.append((f, None, False)); continue
                    img = imread_unicode(f)
                    roi = roi_of(img) if img is not None else None
                    if roi is None:
                        imgs_meta.append((f, gt, False)); continue
                    idx = len(imgs_meta)
                    imgs_meta.append((f, gt, True))
                    strips.append(ann(roi, 0)); owner.append(idx)
                    strips.append(ann(roi, 90)); owner.append(idx)
                # 一次推論整批 strips
                best = {}            # img idx -> (label, conf)
                if strips:
                    rs = m.predict(strips, imgsz=640, verbose=False, device=dev)
                    for s_i, r in enumerate(rs):
                        p = r.probs.data.cpu().numpy()
                        j = int(np.argmax(p))
                        lab, cf = m.names[j], float(p[j])
                        oi = owner[s_i]
                        if oi not in best or cf > best[oi][1]:
                            best[oi] = (lab, cf)
                # 統計 + 存誤判
                for idx, (f, gt, ok_roi) in enumerate(imgs_meta):
                    if gt is None:
                        pfail += 1; continue
                    if not ok_roi:
                        hough += 1; continue
                    pred = best[idx][0]
                    n += 1
                    bm = by_mold[gt]; bm["n"] += 1
                    if pred == gt:
                        ok += 1; bm["ok"] += 1
                    else:
                        bm["conf"][pred] += 1
                        dst_dir = OUT / f.parent.relative_to(SRC)
                        dst_dir.mkdir(parents=True, exist_ok=True)
                        try:
                            shutil.copy2(f, dst_dir / f"pred-{pred}__{f.name}")
                        except Exception as e:
                            print(f"  [copy fail] {f}: {e}", flush=True)
                done += len(batch)
                if done % 1000 < BIMG:
                    print(f"    {rel}: {done}/{len(files)}  acc={ok/max(1,n)*100:.2f}%  "
                          f"hough_fail={hough}  ({time.time()-t0:.0f}s)", flush=True)

            results[rel] = {
                "folder_mold": mold, "n": n, "ok": ok, "acc": ok / max(1, n),
                "hough_fail": hough, "parse_fail": pfail,
                "by_mold": {k: {"n": v["n"], "ok": v["ok"],
                                "conf": dict(v["conf"].most_common())}
                            for k, v in sorted(by_mold.items())},
            }
            foreign = {k: v["n"] for k, v in by_mold.items() if k != mold}
            allconf = Counter()
            for v in by_mold.values():
                allconf.update(v["conf"])
            print(f"  ✔ {rel}: {ok}/{n} = {ok/max(1,n)*100:.2f}%  hough_fail={hough}  "
                  f"混入={foreign if foreign else '無'}  誤判={dict(allconf.most_common(5))}",
                  flush=True)
            (OUT / "_準確度統計.json").write_text(
                json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

    # 匯總表
    write_summary(results)
    print(f"\n總耗時 {time.time()-t0:.0f}s", flush=True)


def write_summary(results):
    # per TRUE mold（跨資料夾聚合；真模號來自檔名）
    by_mold = defaultdict(lambda: {"n": 0, "ok": 0, "conf": Counter()})
    for r in results.values():
        for mk, mv in r["by_mold"].items():
            g = by_mold[mk]
            g["n"] += mv["n"]; g["ok"] += mv["ok"]
            for k, v in mv["conf"].items():
                g["conf"][k] += v
    tot_n = sum(r["n"] for r in results.values())
    tot_ok = sum(r["ok"] for r in results.values())
    tot_h = sum(r["hough_fail"] for r in results.values())
    tot_p = sum(r["parse_fail"] for r in results.values())

    lines = ["# V9.3 模號 全量離線準確度（穩定圖片區）", ""]
    lines.append(f"- 權重：`weights_for('V9.3')[0]` = v9.2 模號（v9.3 只改穴號）")
    lines.append(f"- 推論：環狀 warpPolar R_INNER={R_INNER} + 2-pass（0°/90° 取最高信心），比照部署")
    lines.append(f"- **GT＝檔名真實模號**（非資料夾名；M101_3 內含真 M83 圖，用資料夾名會誤判）")
    lines.append(f"- 總計：**{tot_ok}/{tot_n} = {tot_ok/max(1,tot_n)*100:.2f}%**"
                 f"（find_circle 失敗 {tot_h} 張、檔名無法解析 {tot_p} 張，皆未計入）")
    lines.append("")
    lines.append("## 每（真實）模號")
    lines.append("| 模號 | 正確/總數 | 準確率 | 主要誤判 |")
    lines.append("|---|---|---|---|")
    for mold in sorted(by_mold):
        g = by_mold[mold]
        top = ", ".join(f"{k}×{v}" for k, v in g["conf"].most_common(4)) or "—"
        lines.append(f"| {mold} | {g['ok']}/{g['n']} | "
                     f"{g['ok']/max(1,g['n'])*100:.2f}% | {top} |")
    lines.append("")
    lines.append("## 每資料夾（模號_數字）")
    lines.append("| 資料夾 | 正確/總數 | 準確率 | hough失敗 | 夾內混入 | 主要誤判 |")
    lines.append("|---|---|---|---|---|---|")
    for rel in sorted(results):
        r = results[rel]
        allconf = Counter()
        foreign = {}
        for mk, mv in r["by_mold"].items():
            allconf.update(mv["conf"])
            if mk != r["folder_mold"]:
                foreign[mk] = mv["n"]
        top = ", ".join(f"{k}×{v}" for k, v in allconf.most_common(4)) or "—"
        fo = ", ".join(f"{k}:{v}" for k, v in foreign.items()) or "—"
        lines.append(f"| {rel} | {r['ok']}/{r['n']} | "
                     f"{r['acc']*100:.2f}% | {r['hough_fail']} | {fo} | {top} |")
    (OUT / "_準確度統計.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
