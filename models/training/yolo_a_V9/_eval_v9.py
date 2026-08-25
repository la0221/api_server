# -*- coding: utf-8 -*-
"""V9 A 軸驗證評估：v6.7.2 vs v6.7.3 vs v9 在 data_v671/mohao/val 每模準確率。
判準：v9 對每個模都要 >= v6.7.2（零退步），且 M17 不掉。
另：data4/M17（完全沒進訓練）ROI 化當「未見」探針，測 v9 M17 泛化。
Run: python _eval_v9.py --device 0
"""
from __future__ import annotations
import argparse, sys, tempfile
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

VAL = REPO/"data_v671"/"mohao"/"val"
D4_M17 = REPO/"data4"/"M17"
WEIGHTS = {
 "v6.7.2": REPO/"OCR"/"yolo_a_V6.7.2"/"runs"/"mohao"/"weights"/"best.pt",
 "v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"mohao"/"weights"/"best.pt",
 "v9":     HERE/"runs"/"mohao"/"weights"/"best.pt",
}

def top1(model, strip, dev):
    r = model.predict(strip, imgsz=640, verbose=False, device=dev)[0]
    p = r.probs.data.cpu().numpy(); i = int(np.argmax(p)); return r.names[i], float(p[i])

def eval_val(model, dev):
    """回傳 {cls: (ok,n)} on data_v671 val（strip=已ROI, do_rotate=False）。"""
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

def eval_data4_m17(model, dev):
    """data4/M17 原圖 → find_circle ROI → annulus → 預測是否 M17。"""
    ok = n = miss = 0; wrong = Counter()
    for f in sorted(D4_M17.rglob("*.jpg")):
        img = imread_unicode(f)
        if img is None: continue
        circ = find_circle(img)
        if circ is None: miss += 1; continue
        cx, cy, r = circ
        roi = white_pad_square(img[max(0,cy-r):cy+r, max(0,cx-r):cx+r], target=2*r)
        strip = annulus_polar(roi, do_rotate=False, size=640, r_inner=R_INNER)
        pred, _ = top1(model, strip, dev); n += 1
        if pred == "M17": ok += 1
        else: wrong[pred] += 1
    return ok, n, miss, wrong

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0"); a = ap.parse_args()
    from ultralytics import YOLO
    tags = [t for t in WEIGHTS if WEIGHTS[t].exists()]
    print("評估權重:", tags)
    allres = {}
    for t in tags:
        print(f"  載入 {t} ..."); m = YOLO(str(WEIGHTS[t])); allres[t] = eval_val(m, a.device)

    classes = sorted(allres[tags[0]].keys())
    base = "v6.7.2" if "v6.7.2" in tags else tags[0]
    print(f"\n===== 每模準確率（data_v671/mohao/val），零退步基準={base} =====")
    hdr = "模號   " + "".join(f"{t:>12s}" for t in tags) + "   v9退步?"
    print(hdr); print("-"*len(hdr))
    regress = []
    for c in classes:
        row = f"{c:5s} "
        accs = {}
        for t in tags:
            ok, n = allres[t][c]; acc = ok/n*100 if n else 0; accs[t] = acc
            row += f"  {ok:3d}/{n:3d}={acc:5.1f}"
        flag = ""
        if "v9" in tags and base in tags:
            d = accs["v9"] - accs[base]
            if d < -1e-6: flag = f"  ↓{-d:.1f}%"; regress.append((c, d))
            elif d > 1e-6: flag = f"  ↑{d:.1f}%"
            else: flag = "  ="
        print(row + flag)

    if "v9" in tags:
        print("\n===== data4/M17 未見探針（v9）=====")
        m = YOLO(str(WEIGHTS["v9"]))
        ok, n, miss, wrong = eval_data4_m17(m, a.device)
        print(f"  M17 正確 {ok}/{n} = {ok/n*100:.1f}%   find_circle 抓不到 {miss}")
        if wrong: print(f"  誤判去向: {dict(wrong)}")

    print("\n===== A 軸判定 =====")
    if "v9" in tags and base in tags:
        if not regress:
            print(f"  ✅ 零退步：v9 對每個模都 >= {base}。全量重訓未發生遺忘。")
        else:
            print(f"  ⚠ 有 {len(regress)} 模退步 vs {base}：")
            for c, d in sorted(regress, key=lambda x: x[1]):
                print(f"     {c}: {d:+.1f}%")

if __name__ == "__main__":
    main()
