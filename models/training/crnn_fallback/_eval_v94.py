# -*- coding: utf-8 -*-
"""V9.4 守門：v9.2模號 + v9.4穴號,前處理區全量(★排除訓練用的難例 stem,避免洩漏)。
  重點檢查：穴號有沒有吸收槽(某類佔比過高)、每穴號準確度、與 v9/v9.3/CRNN 對照。

Run: & lens-gpu python _eval_v94.py --device 0
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, "D:/OCR_demo")
from v6_preprocess import imread_unicode          # noqa
from app.config import weights_for                 # noqa

SRC = Path("D:/模號穴號-穩定圖片區/前處理區")
OUT = Path("D:/tmp/v9.3vsrcnn")
V94_XU = REPO / "OCR" / "yolo_a_V9.4" / "runs_v94" / "xuehao" / "weights" / "best.pt"
V94_MO = REPO / "OCR" / "yolo_a_V9.4" / "runs_v94" / "mohao" / "weights" / "best.pt"
STEMS = REPO / "OCR" / "yolo_a_V9.4" / "_v94_trained_error_stems.txt"
MOLDS = ["M101", "M17", "M28", "M54", "M83"]
KEY = re.compile(r"(M\d+)-(\d+)")
BIMG = 24


def v(clf, strips):
    out = []
    for r in clf.predict(strips, imgsz=640, verbose=False, device=0):
        p = r.probs.data.cpu().numpy(); i = int(p.argmax())
        out.append((clf.names[i], float(p[i])))
    return out


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0")
    args = ap.parse_args()
    from ultralytics import YOLO
    mo_w = V94_MO if V94_MO.exists() else weights_for("V9.2")[0]
    clf_mo = YOLO(str(mo_w))
    clf_xu = YOLO(str(V94_XU))
    excl = set(STEMS.read_text(encoding="utf-8").split()) if STEMS.exists() else set()
    print(f"[V9.4 守門] 模號={mo_w}\n  穴號={V94_XU}\n  排除訓練 stem {len(excl)} 個", flush=True)

    per = defaultdict(lambda: {"n": 0, "mo": 0, "xu": 0, "both": 0})
    xu_pred = Counter(); t0 = time.time(); N = 0
    for mold in MOLDS:
        p0s = [p for p in sorted((SRC / mold).rglob("*_p0.png"))
               if p.name.replace("_p0.png", "") not in excl]
        for bi in range(0, len(p0s), BIMG):
            batch = p0s[bi:bi + BIMG]
            metas = []
            for p0 in batch:
                m = KEY.search(p0.name)
                if not m:
                    continue
                s0 = imread_unicode(p0)
                if s0 is None:
                    continue
                p90 = p0.with_name(p0.name.replace("_p0.png", "_p90.png"))
                s90 = imread_unicode(p90) if p90.exists() else s0
                metas.append((m.group(1), m.group(2), s0, s90))
            if not metas:
                continue
            s0s = [x[2] for x in metas]; s90s = [x[3] for x in metas]
            mo0, mo9 = v(clf_mo, s0s), v(clf_mo, s90s)
            xu0, xu9 = v(clf_xu, s0s), v(clf_xu, s90s)
            for j, (gmo, gxu, _, _) in enumerate(metas):
                vmo = (mo0[j] if mo0[j][1] >= mo9[j][1] else mo9[j])[0]
                vxu = (xu0[j] if xu0[j][1] >= xu9[j][1] else xu9[j])[0]
                xu_pred[vxu] += 1
                c = per[mold]; c["n"] += 1; N += 1
                mo_ok, xu_ok = vmo == gmo, vxu == gxu
                c["mo"] += mo_ok; c["xu"] += xu_ok; c["both"] += mo_ok and xu_ok
        a = per[mold]
        print(f"  ✔ {mold}: n={a['n']} 雙對={a['both']/max(1,a['n'])*100:.1f}% "
              f"穴號={a['xu']/max(1,a['n'])*100:.1f}% ({time.time()-t0:.0f}s)", flush=True)

    tot = Counter()
    for c in per.values():
        for k in ("n", "mo", "xu", "both"):
            tot[k] += c[k]
    n = max(1, tot["n"])
    L = ["# V9.4 守門(v9.2模號+v9.4穴號) — 前處理區(排除訓練難例)", ""]
    L.append(f"- N={tot['n']}(已排除訓練用 {len(excl)} stem)")
    L.append("")
    L.append("## 三方雙對對照")
    L.append("| 指標 | **v9.4** | v9混搭 | v9.3 | CRNN |")
    L.append("|---|---|---|---|---|")
    L.append(f"| 雙對 | **{tot['both']/n*100:.2f}%** | 99.14% | 24.94% | 96.14% |")
    L.append(f"| 模號 | {tot['mo']/n*100:.2f}% | 99.80% | 99.80% | 98.30% |")
    L.append(f"| 穴號 | **{tot['xu']/n*100:.2f}%** | 99.33% | 24.95% | 97.54% |")
    L.append("")
    L.append(f"**v9.4 穴號預測分佈(前6)**：{xu_pred.most_common(6)}")
    L.append(f"　→ 最大佔比類 **{xu_pred.most_common(1)[0][0]}={xu_pred.most_common(1)[0][1]/n*100:.1f}%**"
             f"（吸收槽檢查：v9.3 曾 04=73.1%；健康應 ≈ 各類真實佔比 ~5-6%）")
    L.append("")
    L.append("## 每模號")
    L.append("| 模號 | N | v9.4雙對 | v9.4穴號 |")
    L.append("|---|---|---|---|")
    for mo in MOLDS:
        c = per[mo]; nn = max(1, c["n"])
        L.append(f"| {mo} | {c['n']} | {c['both']/nn*100:.1f}% | {c['xu']/nn*100:.1f}% |")
    (OUT / "_v94_summary.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L))
    print(f"\n耗時 {time.time()-t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
