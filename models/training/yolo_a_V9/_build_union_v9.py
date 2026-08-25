# -*- coding: utf-8 -*-
"""建立【完整聯集】v9 資料集(mohao+xuehao)。修正上次只用 data_v671+data3/M17 的缺失。
聯集所有真實擷取源 → 依 stem 去重 → 每(模,穴)格上限 K、跨來源輪流取樣(保外觀多樣) →
現場失敗格留 30% 當 gate holdout(不訓) → 85/15 split → 寫 data_v9/{mohao,xuehao}。
data_v671 已是 ROI 直接複製；其餘 find_circle ROI 化。
Run: python _build_union_v9.py
"""
from __future__ import annotations
import sys, os, re, glob, shutil, random
from pathlib import Path
from collections import defaultdict
import cv2, numpy as np
HERE = Path(__file__).resolve().parent; REPO = HERE.parents[1]
sys.path.insert(0, str(REPO/"OCR"/"yolo_a_V6"))
from v6_preprocess import imread_unicode, find_circle, white_pad_square

EXT=(".jpg",".jpeg",".png",".bmp")
# (name, root, need_roi)  need_roi=False 代表已是 ROI(data_v671)直接複製
SOURCES=[
 ("v671", REPO/"data_v671"/"mohao", False),
 ("data3", REPO/"data3", True),
 ("data4", REPO/"data4", True),
 ("stable", Path(r"D:\模號穴號-穩定圖片區"), True),
 ("f0706", Path(r"D:\OCR_demo\output\2026-07-06"), True),
 ("f0707", Path(r"D:\OCR_demo\output\2026-07-07"), True),
 ("f0707b", Path(r"D:\OCR_demo\output\2026-07-07_V9 vs V6.7"), True),
 ("err", REPO/"錯誤", True),
 ("errset", REPO/"錯誤集", True),
]
GATE_CELLS={("M28","01"),("M28","02"),("M17","11"),("M54","06"),("M54","08")}  # 現場失敗格→留holdout
K=150; VAL_FRAC=0.15; GATE_FRAC=0.30; SEED=0
OUT_M=REPO/"data_v9"/"mohao"; OUT_X=REPO/"data_v9"/"xuehao"
GATE=REPO/"data_v9"/"_gate_field"   # 現場 holdout(raw，不訓)
rng=random.Random(SEED)

def truth(path, fn):
    m=re.search(r"exp_M(\d+)-(\d+)", fn) or re.search(r"^(?:ng|d3M17_|fld_)?M(\d+)[-_](\d+)", fn)
    if m: return "M"+m.group(1), m.group(2).zfill(2)
    mm=re.search(r"[\\/]M(\d+)(?:新|[\\/]|$)", str(path)); cc=re.findall(r"[\\/](\d{2})(?:[\\/]|$)", str(path))
    return ("M"+mm.group(1) if mm else None), (cc[-1] if cc else None)

def roi_save(src, outp, need_roi):
    if not need_roi:
        shutil.copy2(src, outp); return True
    img=imread_unicode(src)
    if img is None: return False
    c=find_circle(img)
    if c is None: return False
    cx,cy,r=c; roi=white_pad_square(img[max(0,cy-r):cy+r,max(0,cx-r):cx+r],target=2*r)
    ok,buf=cv2.imencode(".jpg",roi,[cv2.IMWRITE_JPEG_QUALITY,92])
    if not ok: return False
    buf.tofile(str(outp)); return True

# 1) 收集所有 (mold,cav,source,path)，去重
print("[1] 收集+去重 ...")
cells=defaultdict(lambda: defaultdict(list))  # (mold,cav) -> source -> [paths]
seen=set(); ntot=0
for name,root,need in SOURCES:
    if not root.exists(): print(f"    (跳過不存在 {name})"); continue
    cnt=0
    for dp,_,fns in os.walk(root):
        for f in fns:
            if not f.lower().endswith(EXT): continue
            mo,ca=truth(dp,f)
            if not mo or not ca or ca=="NG":
                # NG 類特殊：mohao 有 NG 夾
                pass
            stem=re.sub(r"(__r\d+_1)?$","",Path(f).stem)
            key=(mo,ca,stem)
            if key in seen: continue
            seen.add(key)
            if mo and ca:
                cells[(mo,ca)][name].append((os.path.join(dp,f),need)); cnt+=1
    print(f"    {name}: +{cnt}"); ntot+=cnt
print(f"    去重後總計 {ntot}")

# NG 類(只 mohao)：從 data_v671/mohao/NG
ng_src=[(p,False) for p in glob.glob(str(REPO/"data_v671"/"mohao"/"NG"/"*.jpg"))]

# 2) 每格：抽 gate holdout(現場失敗格) → 其餘跨來源輪流取樣至 K → split
print(f"[2] 每格上限 K={K}、跨來源輪流、現場格留 {int(GATE_FRAC*100)}% gate ...")
for d in (OUT_M,OUT_X,GATE):
    if d.exists(): shutil.rmtree(d)
sel_train=[]; sel_val=[]; gate=[]   # each: (path,need,mold,cav)
for (mo,ca),bysrc in cells.items():
    # gate holdout
    if (mo,ca) in GATE_CELLS:
        for name,lst in bysrc.items():
            rng.shuffle(lst); ng=int(len(lst)*GATE_FRAC)
            gate += [(p,nd,mo,ca) for p,nd in lst[:ng]]
            bysrc[name]=lst[ng:]
    # 跨來源輪流取 K
    pools={name:list(lst) for name,lst in bysrc.items() if lst}
    for lst in pools.values(): rng.shuffle(lst)
    picked=[]; order=list(pools)
    while len(picked)<K and any(pools.values()):
        for name in order:
            if pools[name]:
                picked.append(pools[name].pop())
                if len(picked)>=K: break
    rng.shuffle(picked); nv=max(1,int(len(picked)*VAL_FRAC))
    sel_val += [(p,nd,mo,ca) for p,nd in picked[:nv]]
    sel_train += [(p,nd,mo,ca) for p,nd in picked[nv:]]

# 3) 寫檔
def write(items, split):
    okm=okx=0
    for i,(p,nd,mo,ca) in enumerate(items):
        stem=Path(p).stem
        dm=OUT_M/split/mo; dm.mkdir(parents=True,exist_ok=True)
        if roi_save(p, dm/f"{stem}__u{i}.jpg", nd): okm+=1
        dx=OUT_X/split/ca; dx.mkdir(parents=True,exist_ok=True)
        roi_save(p, dx/f"{stem}__u{i}.jpg", nd); okx+=1
    return okm
print("[3] 寫 train ..."); ntr=write(sel_train,"train")
print("[3] 寫 val ...");   nva=write(sel_val,"val")
# NG 只寫 mohao
(OUT_M/"train"/"NG").mkdir(parents=True,exist_ok=True); (OUT_M/"val"/"NG").mkdir(parents=True,exist_ok=True)
rng.shuffle(ng_src); ngv=max(1,int(len(ng_src)*VAL_FRAC))
for i,(p,nd) in enumerate(ng_src[ngv:]): roi_save(p,OUT_M/"train"/"NG"/f"{Path(p).stem}__u{i}.jpg",nd)
for i,(p,nd) in enumerate(ng_src[:ngv]): roi_save(p,OUT_M/"val"/"NG"/f"{Path(p).stem}__u{i}.jpg",nd)
# gate holdout 存 raw
GATE.mkdir(parents=True,exist_ok=True)
for i,(p,nd,mo,ca) in enumerate(gate):
    dd=GATE/f"{mo}_{ca}"; dd.mkdir(parents=True,exist_ok=True); shutil.copy2(p, dd/os.path.basename(p))

# 4) 統計
print("\n[4] 完成。data_v9/mohao 各模 train 張數：")
for mo in sorted([d.name for d in (OUT_M/"train").iterdir() if d.is_dir()]):
    ntr_m=len(list((OUT_M/"train"/mo).glob("*.jpg"))); nva_m=len(list((OUT_M/"val"/mo).glob("*.jpg")))
    print(f"    {mo:5s} train={ntr_m:4d} val={nva_m:3d}")
print(f"  xuehao 穴號數: {len(list((OUT_X/'train').iterdir()))}")
print(f"  gate holdout: {sum(len(list(d.glob('*.jpg'))) for d in GATE.iterdir())} 張 @ {GATE}")
print(f"OUT -> {OUT_M} / {OUT_X}")
