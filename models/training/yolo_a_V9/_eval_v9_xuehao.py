# -*- coding: utf-8 -*-
"""V9 穴號 評估：v6.7.3 xuehao vs v9 xuehao 在 data_v671/xuehao/val 每穴號準確率。
判準：v9 對每個穴號都要 >= 現行版（零退步）。
另：data4/M17（沒進訓練）依穴號 ROI 化當未見探針，測 v9 M17 各穴號泛化。
Run: python _eval_v9_xuehao.py --device 0
"""
from __future__ import annotations
import argparse, sys
from pathlib import Path
from collections import Counter
import cv2, numpy as np
cv2.setNumThreads(0)
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6"))
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6.7"))
from v67_dataset import R_INNER, annulus_polar
from v6_preprocess import imread_unicode, find_circle, white_pad_square

VAL = REPO/"data_v671"/"xuehao"/"val"
D4_M17 = REPO/"data4"/"M17"
WEIGHTS = {
 "v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"xuehao"/"weights"/"best.pt",
 "v9":     HERE/"runs"/"xuehao"/"weights"/"best.pt",
}
def top1(model, strip, dev):
    r = model.predict(strip, imgsz=640, verbose=False, device=dev)[0]
    p = r.probs.data.cpu().numpy(); i = int(np.argmax(p)); return r.names[i], float(p[i])

def eval_val(model, dev):
    res = {}
    for cd in sorted([d for d in VAL.iterdir() if d.is_dir()]):
        ok = n = 0
        for f in sorted(cd.glob("*.jpg")):
            roi = cv2.imread(str(f))
            if roi is None: continue
            strip = annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)
            pred, _ = top1(model, strip, dev); n += 1; ok += (pred == cd.name)
        res[cd.name] = (ok, n)
    return res

def eval_data4(model, dev):
    """data4/M17/<穴號> 原圖 → ROI → 預測穴號是否==資料夾。"""
    per = {}
    for cav_dir in sorted([d for d in D4_M17.iterdir() if d.is_dir()]):
        cav = cav_dir.name; ok = n = miss = 0; wrong = Counter()
        for f in sorted(cav_dir.rglob("*.jpg")):
            img = imread_unicode(f)
            if img is None: continue
            circ = find_circle(img)
            if circ is None: miss += 1; continue
            cx, cy, r = circ
            roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
            strip = annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)
            pred, _ = top1(model, strip, dev); n += 1
            if pred == cav: ok += 1
            else: wrong[pred] += 1
        per[cav] = (ok, n, miss, dict(wrong))
    return per

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0"); a = ap.parse_args()
    from ultralytics import YOLO
    tags = [t for t in WEIGHTS if WEIGHTS[t].exists()]
    print("評估權重:", tags)
    allres = {t: eval_val(YOLO(str(WEIGHTS[t])), a.device) for t in tags}
    cavs = sorted(allres[tags[0]].keys())
    base = "v6.7.3" if "v6.7.3" in tags else tags[0]
    print(f"\n===== 每穴號準確率（data_v671/xuehao/val），零退步基準={base} =====")
    hdr = "穴號 " + "".join(f"{t:>12s}" for t in tags) + "   v9退步?"
    print(hdr); print("-"*len(hdr))
    regress = []
    for c in cavs:
        row = f"{c:4s}"; accs = {}
        for t in tags:
            ok, n = allres[t][c]; acc = ok/n*100 if n else 0; accs[t] = acc
            row += f"  {ok:3d}/{n:3d}={acc:5.1f}"
        if "v9" in tags and base in tags:
            d = accs["v9"] - accs[base]
            row += (f"  ↓{-d:.1f}%" if d < -1e-6 else (f"  ↑{d:.1f}%" if d > 1e-6 else "  ="))
            if d < -1e-6: regress.append((c, d))
        print(row)

    if "v9" in tags:
        print("\n===== data4/M17 未見探針（v9，依穴號）=====")
        per = eval_data4(YOLO(str(WEIGHTS["v9"])), a.device)
        tot_ok = tot_n = 0
        for c in sorted(per):
            ok, n, miss, wrong = per[c]; tot_ok += ok; tot_n += n
            w = f"  誤判{wrong}" if wrong else ""
            print(f"  穴{c}: {ok}/{n}={ok/n*100 if n else 0:5.1f}%  (漏{miss}){w}")
        print(f"  合計 {tot_ok}/{tot_n} = {tot_ok/tot_n*100 if tot_n else 0:.1f}%")

    print("\n===== A 軸判定（穴號）=====")
    if "v9" in tags and base in tags:
        if not regress: print(f"  ✅ 零退步：v9 每穴號都 >= {base}。")
        else:
            print(f"  ⚠ 有 {len(regress)} 穴號退步 vs {base}：")
            for c, d in sorted(regress, key=lambda x: x[1]): print(f"     穴{c}: {d:+.1f}%")

if __name__ == "__main__":
    main()
