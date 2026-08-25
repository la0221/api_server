# -*- coding: utf-8 -*-
"""探測穩定圖片區三模結構：各模的穴號葉夾與張數、影像尺寸。"""
import os, cv2, numpy as np
from pathlib import Path
ROOTS = {
 "M101": r"D:\模號穴號-穩定圖片區\M101\M101收圖1\M",
 "M17":  r"D:\模號穴號-穩定圖片區\M17\M17新的ROI\M17新的ROI_1\M17",
 "M83":  r"D:\模號穴號-穩定圖片區\M83\第二包",
}
EXT = (".jpg", ".jpeg", ".png", ".bmp")
def imread_u(p):
    try: return cv2.imdecode(np.fromfile(p, np.uint8), 1)
    except Exception: return None
for mold, root in ROOTS.items():
    print(f"\n===== {mold} : {root} =====")
    if not os.path.isdir(root):
        print("  !! 路徑不存在"); continue
    # 收集所有含影像的葉夾
    leaf = {}
    for dp, dn, fn in os.walk(root):
        imgs = [f for f in fn if f.lower().endswith(EXT)]
        if imgs:
            rel = os.path.relpath(dp, root)
            leaf[rel] = len(imgs)
    if not leaf:
        print("  (無影像檔)"); continue
    for rel in sorted(leaf):
        print(f"    {rel:40s} {leaf[rel]:4d} 張")
    # 抽一張看尺寸
    for dp, dn, fn in os.walk(root):
        s = [f for f in fn if f.lower().endswith(EXT)]
        if s:
            im = imread_u(os.path.join(dp, s[0]))
            print(f"    尺寸抽查 {s[0][:36]} -> {None if im is None else im.shape}")
            break
