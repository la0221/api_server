# -*- coding: utf-8 -*-
"""列出 cs vs py 不一致的影像，並印出該圖在 cv2 端的 Hough 圓（cx,cy,r）供比對。"""
import json
from pathlib import Path
import cv2
import numpy as np

HERE = Path(__file__).parent
cs = json.loads((HERE / "cs_golden.json").read_text(encoding="utf-8"))
py = json.loads((HERE / "py_golden.json").read_text(encoding="utf-8"))
HOUGH = dict(dp=1, minDist=100, param1=200, param2=30, minRadius=200, maxRadius=300)


def imread_unicode(p):
    return cv2.imdecode(np.fromfile(p, dtype=np.uint8), cv2.IMREAD_COLOR)


def circle(p):
    im = imread_unicode(p)
    g = cv2.medianBlur(cv2.cvtColor(im, cv2.COLOR_BGR2GRAY), 3)
    c = cv2.HoughCircles(g, cv2.HOUGH_GRADIENT, **HOUGH)
    if c is None:
        return "NO CIRCLE"
    b = max(c[0], key=lambda x: x[2])
    return f"cx={b[0]:.1f} cy={b[1]:.1f} r={b[2]:.1f}  (n_circles={len(c[0])})"


for f, c in cs.items():
    p = py[f]
    if c["mohao"] != p["mohao"] or c["xuehao"] != p["xuehao"] or bool(c["present"]) != bool(p["present"]):
        print("FILE:", f)
        print(f"  truth      : {c['mold_truth']}/{c['cav_truth']}")
        print(f"  C#  mohao={c['mohao']}@{c['conf_m']}  xuehao={c['xuehao']}@{c['conf_x']}  present={c['present']}")
        print(f"  PY  mohao={p['mohao']}@{p['conf_m']}  xuehao={p['xuehao']}@{p['conf_x']}  present={p['present']}")
        print("  cv2 Hough  :", circle(f))
