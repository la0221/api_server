# -*- coding: utf-8 -*-
"""v9 強化版 gate 驗收：用【現場 holdout（不曾進訓練）】比 v9(new) vs v6.7.3。
  mohao: M28_01/M28_02/M54_06/M54_08 → 模號對率
  xuehao: M17_11 → 穴號對率（就是 11→03 出事的格）
  另：data_v671 val 每類零退步。
Run: python _gate_v9.py --device 0
"""
from __future__ import annotations
import argparse, sys, glob, os
from pathlib import Path
from collections import Counter
import cv2, numpy as np
cv2.setNumThreads(0)
HERE = Path(__file__).resolve().parent; REPO = HERE.parents[1]
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6")); sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6.7"))
from v67_dataset import R_INNER, annulus_polar
from v6_preprocess import imread_unicode, find_circle, white_pad_square
GATE = REPO/"data_v9"/"_gate_field"
VAL_M = REPO/"data_v671"/"mohao"/"val"; VAL_X = REPO/"data_v671"/"xuehao"/"val"
W = {
 "mohao": {"v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"mohao"/"weights"/"best.pt",
           "v9":     HERE/"runs"/"mohao"/"weights"/"best.pt"},
 "xuehao":{"v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"xuehao"/"weights"/"best.pt",
           "v9":     HERE/"runs"/"xuehao"/"weights"/"best.pt"},
}
def strip_raw(p):
    img=imread_unicode(p)
    if img is None: return None
    c=find_circle(img)
    if c is None: return None
    cx,cy,r=c; roi=white_pad_square(img[max(0,cy-r):cy+r,max(0,cx-r):cx+r],target=2*r)
    return annulus_polar(roi,do_rotate=False,size=640,r_inner=R_INNER)
def top1(m,s,dev):
    r=m.predict(s,imgsz=640,verbose=False,device=dev)[0]
    p=r.probs.data.cpu().numpy(); i=int(np.argmax(p)); return r.names[i]
def gate_cell(models, cell, target, is_mohao, dev):
    strips=[]
    for p in glob.glob(str(GATE/cell/"*.jpg")):
        s=strip_raw(p)
        if s is not None: strips.append(s)
    print(f"  [{cell}] target={target}  n={len(strips)}")
    for t,m in models.items():
        ok=0; wr=Counter()
        for s in strips:
            pr=top1(m,s,dev)
            if pr==target: ok+=1
            else: wr[pr]+=1
        print(f"      {t:7s}: {ok}/{len(strips)} = {ok/len(strips)*100 if strips else 0:5.1f}%  誤判={dict(wr.most_common(4))}")
def val_regress(models, VAL, dev):
    from collections import defaultdict
    res=defaultdict(dict)
    for cd in sorted([d for d in VAL.iterdir() if d.is_dir()]):
        for t,m in models.items():
            ok=n=0
            for f in sorted(cd.glob("*.jpg")):
                roi=cv2.imread(str(f))
                if roi is None: continue
                s=annulus_polar(roi,do_rotate=False,size=640,r_inner=R_INNER)
                n+=1; ok+=(top1(m,s,dev)==cd.name)
            res[cd.name][t]=(ok,n)
    tags=list(models); reg=[]
    print(f"  每類(v9 vs v6.7.3)：")
    for c in sorted(res):
        a=res[c]; s=" ".join(f"{t}={a[t][0]}/{a[t][1]}" for t in tags)
        d=(a["v9"][0]/max(1,a["v9"][1])-a["v6.7.3"][0]/max(1,a["v6.7.3"][1]))*100 if "v9" in a and "v6.7.3" in a else 0
        flag = f" ↓{-d:.1f}" if d<-1e-6 else (" ↑" if d>1e-6 else "")
        if d<-1e-6: reg.append((c,d))
        print(f"    {c:5s} {s}{flag}")
    print(f"  → {'✅零退步' if not reg else '⚠退步:'+str([(c,round(d,1)) for c,d in reg])}")

def main():
    ap=argparse.ArgumentParser(); ap.add_argument("--device",default="0"); ap.add_argument("--head",default="both"); a=ap.parse_args()
    from ultralytics import YOLO
    heads = ["mohao","xuehao"] if a.head=="both" else [a.head]
    for head in heads:
        ws={t:p for t,p in W[head].items() if p.exists()}
        if "v9" not in ws: print(f"\n### {head}: v9 權重尚無，跳過"); continue
        models={t:YOLO(str(p)) for t,p in ws.items()}
        print(f"\n########## {head} gate ##########")
        if head=="mohao":
            for cell,tgt in [("M28_01","M28"),("M28_02","M28"),("M54_06","M54"),("M54_08","M54")]:
                gate_cell(models,cell,tgt,True,a.device)
            print("\n-- data_v671/mohao/val 零退步 --"); val_regress(models,VAL_M,a.device)
        else:
            gate_cell(models,"M17_11","11",False,a.device)
            print("\n-- data_v671/xuehao/val 零退步 --"); val_regress(models,VAL_X,a.device)

if __name__ == "__main__":
    main()
