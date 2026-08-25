# -*- coding: utf-8 -*-
"""決定性測：v9 / v9.1 / v9.2 三方對照。
  v9   = 基礎增強、無現場M28        （權重在 OCR/yolo_a_V9/runs）
  v9.1 = 餵現場M28（窮舉外觀）       （權重在 OCR/yolo_a_V9/runs_v91）
  v9.2 = 域隨機化強增強、★無現場M28（權重在 OCR/yolo_a_V9.2/runs）
判準：v9.2 在 M28 holdout 回得來→泛化成立；回不來→分類天花板，該投 S5 讀字。
Run: python _eval_v92.py --device 0
"""
from __future__ import annotations
import argparse, sys, glob
from pathlib import Path
from collections import Counter
import cv2, numpy as np
cv2.setNumThreads(0)
HERE = Path(__file__).resolve().parent      # OCR/yolo_a_V9.2
REPO = HERE.parents[1]
V9DIR = REPO/"OCR"/"yolo_a_V9"
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6"))
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6.7"))
from v67_dataset import R_INNER, annulus_polar
from v6_preprocess import imread_unicode, find_circle, white_pad_square

VAL = REPO/"data_v671"/"mohao"/"val"
HOLD = REPO/"data_v91"/"_m28_field_holdout"
WEIGHTS = {
 "v9":   V9DIR/"runs"/"mohao"/"weights"/"best.pt",
 "v9.1": V9DIR/"runs_v91"/"mohao"/"weights"/"best.pt",
 "v9.2": HERE/"runs"/"mohao"/"weights"/"best.pt",
}
def top1(m, s, dev):
    r = m.predict(s, imgsz=640, verbose=False, device=dev)[0]
    p = r.probs.data.cpu().numpy(); i = int(np.argmax(p)); return r.names[i], float(p[i])
def strip_raw(p):
    img = imread_unicode(p)
    if img is None: return None
    c = find_circle(img)
    if c is None: return None
    cx, cy, r = c; roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
    return annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)
def eval_val(m, dev):
    res = {}
    for cd in sorted([d for d in VAL.iterdir() if d.is_dir()]):
        ok = n = 0
        for f in sorted(cd.glob("*.jpg")):
            roi = cv2.imread(str(f))
            if roi is None: continue
            s = annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)
            pr, _ = top1(m, s, dev); n += 1; ok += (pr == cd.name)
        res[cd.name] = (ok, n)
    return res
def eval_hold(m, dev):
    ok = n = miss = 0; wr = Counter()
    for p in sorted(glob.glob(str(HOLD/"*.jpg"))):
        s = strip_raw(p)
        if s is None: miss += 1; continue
        pr, _ = top1(m, s, dev); n += 1
        if pr == "M28": ok += 1
        else: wr[pr] += 1
    return ok, n, miss, dict(wr)

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0"); a = ap.parse_args()
    from ultralytics import YOLO
    tags = [t for t in WEIGHTS if WEIGHTS[t].exists()]
    print("評估:", tags)
    models = {t: YOLO(str(WEIGHTS[t])) for t in tags}

    print("\n===== M28 field holdout（172 未見現場 M28）=====")
    for t in tags:
        ok, n, miss, wr = eval_hold(models[t], a.device)
        print(f"  [{t:5s}] M28 {ok}/{n} = {ok/n*100 if n else 0:5.1f}%  (漏{miss})  誤判={wr}")

    print("\n===== data_v671/mohao/val 每模（v9.2 零退步檢查）=====")
    R = {t: eval_val(models[t], a.device) for t in tags}
    classes = sorted(R[tags[0]].keys())
    hdr = "模號 " + "".join(f"{t:>11s}" for t in tags)
    print(hdr); print("-"*len(hdr)); reg = []
    for c in classes:
        row = f"{c:5s}"; accs = {}
        for t in tags:
            ok, n = R[t][c]; acc = ok/n*100 if n else 0; accs[t] = acc
            row += f"  {acc:5.1f}"
        if "v9.2" in tags and "v9" in tags and accs["v9.2"] < accs["v9"]-1e-6:
            reg.append((c, accs["v9.2"]-accs["v9"])); row += f"  ↓{accs['v9']-accs['v9.2']:.1f}"
        print(row)
    if reg: print(f"\n  v9.2 vs v9 退步模: {[(c,round(d,1)) for c,d in reg]}")

    print("\n===== 判定 =====")
    if "v9.2" in tags:
        ok, n, _, _ = eval_hold(models["v9.2"], a.device)
        r = ok/n*100 if n else 0
        if r >= 80: print(f"  ✅ 泛化成立：v9.2 純靠強增強(未餵現場M28) 在 holdout={r:.1f}% → 域隨機化能扛未見外觀。")
        elif r >= 30: print(f"  △ 部分泛化：v9.2 holdout={r:.1f}% → 增強有幫助但不足，需混合(增強+少量現場多樣圖)。")
        else: print(f"  ✗ 泛化失敗：v9.2 holdout={r:.1f}% → 分類天花板，讀字(S5)才是解。")

if __name__ == "__main__":
    main()
