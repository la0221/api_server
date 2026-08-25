# -*- coding: utf-8 -*-
"""V9.2+NG 穴號評估：v6.7.3 xuehao vs v9.2n xuehao(19類含NG)
  (1) data_v671/xuehao/val 每穴號(01-18)零退步
  (2) NG val(data_v671/mohao/val/NG) → 是否判 NG（確認 NG 拒收保住）
  (3) _m28_04_holdout(200 未見 M28-04) → 穴號判成 04（04→06 修正）
Run: python _eval_v92n_xuehao.py --device 0
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
VAL = REPO/"data_v671"/"xuehao"/"val"
NGVAL = REPO/"data_v671"/"mohao"/"val"/"NG"
HOLD = REPO/"data_v92"/"_m28_04_holdout"
WEIGHTS = {
 "v6.7.3": REPO/"OCR"/"yolo_a_V6.7.3"/"runs"/"xuehao"/"weights"/"best.pt",
 "v9.2n":  HERE/"runs_v92n"/"xuehao"/"weights"/"best.pt",
}
def top1(m,s,dev):
    r=m.predict(s,imgsz=640,verbose=False,device=dev)[0]; p=r.probs.data.cpu().numpy(); i=int(np.argmax(p)); return r.names[i]
def strip_roi(f):
    roi=cv2.imread(str(f));
    return None if roi is None else annulus_polar(roi,do_rotate=False,size=640,r_inner=R_INNER)
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
            s=strip_roi(f)
            if s is None: continue
            n+=1; ok+=(top1(m,s,dev)==cd.name)
        res[cd.name]=(ok,n)
    return res
def eval_ng(m,dev):
    ok=n=0; wr=Counter()
    for f in sorted(NGVAL.glob("*.jpg")):
        s=strip_roi(f)
        if s is None: continue
        n+=1; pr=top1(m,s,dev)
        if pr=="NG": ok+=1
        else: wr[pr]+=1
    return ok,n,dict(wr)
def eval_hold(m,dev):
    ok=n=miss=0; wr=Counter()
    for p in sorted(glob.glob(str(HOLD/"*.jpg"))):
        s=strip_raw(p)
        if s is None: miss+=1; continue
        n+=1; pr=top1(m,s,dev)
        if pr=="04": ok+=1
        else: wr[pr]+=1
    return ok,n,miss,dict(wr)
def main():
    ap=argparse.ArgumentParser(); ap.add_argument("--device",default="0"); a=ap.parse_args()
    from ultralytics import YOLO
    tags=[t for t in WEIGHTS if WEIGHTS[t].exists()]; print("評估:",tags)
    M={t:YOLO(str(WEIGHTS[t])) for t in tags}
    R={t:eval_val(M[t],a.device) for t in tags}
    cavs=sorted(R[tags[0]].keys()); base="v6.7.3" if "v6.7.3" in tags else tags[0]
    print(f"\n===== xuehao/val 每穴號(01-18)（零退步 vs {base}）=====")
    hdr="穴號 "+"".join(f"{t:>10s}" for t in tags)+"  變化"; print(hdr); print("-"*len(hdr)); reg=[]
    for c in cavs:
        row=f"{c:4s}"; accs={}
        for t in tags:
            ok,n=R[t][c]; acc=ok/n*100 if n else 0; accs[t]=acc; row+=f"  {acc:6.1f}"
        if base in tags and "v9.2n" in tags:
            d=accs["v9.2n"]-accs[base]; row+=(f"  ↓{-d:.1f}" if d<-1e-6 else (f"  ↑{d:.1f}" if d>1e-6 else "  ="))
            if d<-1e-6: reg.append((c,d))
        print(row)
    print("\n===== NG val（確認 NG 拒收保住）=====")
    for t in tags:
        ok,n,wr=eval_ng(M[t],a.device); print(f"  [{t}] NG {ok}/{n}  誤判={wr}")
    print("\n===== _m28_04_holdout（200 未見 M28-04）→ 判 04 =====")
    for t in tags:
        ok,n,miss,wr=eval_hold(M[t],a.device); print(f"  [{t}] 04 {ok}/{n}={ok/n*100 if n else 0:.1f}%  漏{miss}  誤判={wr}")
    print(("\n⚠ v9.2n 穴號退步: "+str([(c,round(d,1)) for c,d in reg])) if reg else f"\n✅ v9.2n 每穴 ≥ {base}")
if __name__=="__main__": main()
