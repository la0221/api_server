# -*- coding: utf-8 -*-
"""V9.2 模號評估：v9.1 vs v9.2
  (1) data_v671/mohao/val 全 20 類零退步
  (2) _m28_04_holdout（200 未見 M28-04 raw）→ M28 是否回來
Run: python _eval_v92.py --device 0
"""
from __future__ import annotations
import argparse, sys, glob
from pathlib import Path
from collections import Counter
import cv2, numpy as np
cv2.setNumThreads(0)
HERE = Path(__file__).resolve().parent; REPO = HERE.parents[1]
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6")); sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6.7"))
from v67_dataset import R_INNER, annulus_polar
from v6_preprocess import imread_unicode, find_circle, white_pad_square
VAL = REPO/"data_v671"/"mohao"/"val"
HOLD = REPO/"data_v92"/"_m28_04_holdout"
WEIGHTS = {
 "v9":   HERE/"runs"/"mohao"/"weights"/"best.pt",
 "v9.1": HERE/"runs_v91"/"mohao"/"weights"/"best.pt",
 "v9.2": HERE/"runs_v92"/"mohao"/"weights"/"best.pt",
}
def top1(m,s,dev):
    r=m.predict(s,imgsz=640,verbose=False,device=dev)[0]; p=r.probs.data.cpu().numpy(); i=int(np.argmax(p)); return r.names[i]
def strip_raw(p):
    img=imread_unicode(p)
    if img is None: return None
    c=find_circle(img)
    if c is None: return None
    cx,cy,r=c; roi=white_pad_square(img[max(0,cy-r):cy+r,max(0,cx-r):cx+r],target=2*r)
    return annulus_polar(roi,do_rotate=False,size=640,r_inner=R_INNER)
def eval_val(m,dev):
    res={}
    for cd in sorted([d for d in VAL.iterdir() if d.is_dir()]):
        ok=n=0
        for f in sorted(cd.glob("*.jpg")):
            roi=cv2.imread(str(f))
            if roi is None: continue
            s=annulus_polar(roi,do_rotate=False,size=640,r_inner=R_INNER)
            n+=1; ok+=(top1(m,s,dev)==cd.name)
        res[cd.name]=(ok,n)
    return res
def eval_hold(m,dev):
    ok=n=miss=0; wr=Counter()
    for p in sorted(glob.glob(str(HOLD/"*.jpg"))):
        s=strip_raw(p)
        if s is None: miss+=1; continue
        pr=top1(m,s,dev); n+=1
        if pr=="M28": ok+=1
        else: wr[pr]+=1
    return ok,n,miss,dict(wr)
def main():
    ap=argparse.ArgumentParser(); ap.add_argument("--device",default="0"); a=ap.parse_args()
    from ultralytics import YOLO
    tags=[t for t in WEIGHTS if WEIGHTS[t].exists()]; print("評估:",tags)
    R={t:eval_val(YOLO(str(WEIGHTS[t])),a.device) for t in tags}
    cls=sorted(R[tags[0]].keys())
    print("\n===== data_v671/mohao/val 每模（零退步 v9.1→v9.2）=====")
    hdr="模號 "+"".join(f"{t:>10s}" for t in tags)+"  v9.2vs9.1"; print(hdr); print("-"*len(hdr)); reg=[]
    for c in cls:
        row=f"{c:5s}"; accs={}
        for t in tags:
            ok,n=R[t][c]; acc=ok/n*100 if n else 0; accs[t]=acc; row+=f"  {acc:6.1f}"
        if "v9.1" in tags and "v9.2" in tags:
            d=accs["v9.2"]-accs["v9.1"]; row+=(f"  ↓{-d:.1f}" if d<-1e-6 else (f"  ↑{d:.1f}" if d>1e-6 else "  ="))
            if d<-1e-6: reg.append((c,d))
        print(row)
    print("\n===== _m28_04_holdout（200 未見 M28-04）→ 判成 M28 比例 =====")
    for t in tags:
        ok,n,miss,wr=eval_hold(YOLO(str(WEIGHTS[t])),a.device)
        print(f"  [{t}] {ok}/{n}={ok/n*100 if n else 0:.1f}%  漏{miss}  誤判={wr}")
    print(("\n⚠ v9.2 退步: "+str([(c,round(d,1)) for c,d in reg])) if reg else "\n✅ v9.2 對每模 ≥ v9.1")
if __name__=="__main__": main()
