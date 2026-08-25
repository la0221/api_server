# -*- coding: utf-8 -*-
"""v9混搭(v9.2模號 + v9穴號) vs CRNN — 前處理區全量雙對。

動機：v9.3 穴號在前處理區塌成 04 吸收槽（73% 判 04）。v9 穴號是 v9.2/v9.3 那三輪
  M28-04 灌圖『之前』的版本，測它有沒有同樣的吸收槽。
CRNN 不重跑：直接沿用 `D:/tmp/v9.3vsrcnn/_raw_stats.json` 既有結果。

  v9.2 模號 = weights_for('V9.2')[0]（= runs_v92/mohao，與 v9.3 同一顆）
  v9 穴號   = V9_XUEHAO_WEIGHTS（runs/xuehao，18 類無 NG）
  兩 head 各 2-pass(p0/p90) 取最高信心。

Run: & lens-gpu python _eval_v9combo.py --device 0
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
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
from app.config import weights_for, V9_XUEHAO_WEIGHTS  # noqa

SRC = Path("D:/模號穴號-穩定圖片區/前處理區")
OUT = Path("D:/tmp/v9.3vsrcnn")
CRNN_STATS = OUT / "_raw_stats.json"      # 沿用既有 CRNN 結果
MOLDS = ["M101", "M17", "M28", "M54", "M83"]
KEY = re.compile(r"(M\d+)-(\d+)")
BIMG = 24
CAP = 25


def v93_batch(clf, strips):
    out = []
    for r in clf.predict(strips, imgsz=640, verbose=False, device=0):
        p = r.probs.data.cpu().numpy(); i = int(p.argmax())
        out.append((clf.names[i], float(p[i])))
    return out


def load_crnn():
    """從既有 _raw_stats.json 取 CRNN per-mold 聚合。"""
    if not CRNN_STATS.exists():
        return None
    d = json.load(open(CRNN_STATS, encoding="utf-8"))
    per = defaultdict(Counter)
    for k, c in d.items():
        mo = k.split("/")[0]
        for f in ("n", "crnn_mo", "crnn_xu", "crnn_both"):
            per[mo][f] += c.get(f, 0)
    return per


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()
    from ultralytics import YOLO
    clf_mo = YOLO(str(weights_for("V9.2")[0]))
    clf_xu = YOLO(str(V9_XUEHAO_WEIGHTS))
    OUT.mkdir(parents=True, exist_ok=True)
    print(f"[v9混搭] 模號={weights_for('V9.2')[0]}\n        穴號={V9_XUEHAO_WEIGHTS}", flush=True)

    def newcell():
        return {"n": 0, "mo": 0, "xu": 0, "both": 0, "xu_pred": Counter(), "copied": 0}
    stats = defaultdict(newcell)
    t0 = time.time(); total = 0

    for mold in MOLDS:
        base = SRC / mold
        if not base.exists():
            continue
        p0s = sorted(base.rglob("*_p0.png"))
        if args.limit:
            byc = defaultdict(list)
            for p in p0s:
                m = KEY.search(p.name)
                if m:
                    byc[m.group(2)].append(p)
            p0s = [p for c in byc for p in byc[c][:args.limit]]
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
                metas.append((p0, m.group(1), m.group(2), s0, s90))
            if not metas:
                continue
            s0s = [x[3] for x in metas]; s90s = [x[4] for x in metas]
            mo0 = v93_batch(clf_mo, s0s); mo9 = v93_batch(clf_mo, s90s)
            xu0 = v93_batch(clf_xu, s0s); xu9 = v93_batch(clf_xu, s90s)
            for j, (p0, gmo, gxu, _, _) in enumerate(metas):
                cell = stats[(mold, gxu)]; cell["n"] += 1; total += 1
                vmo = (mo0[j] if mo0[j][1] >= mo9[j][1] else mo9[j])[0]
                vxu = (xu0[j] if xu0[j][1] >= xu9[j][1] else xu9[j])[0]
                cell["xu_pred"][vxu] += 1
                mo_ok, xu_ok = vmo == gmo, vxu == gxu
                cell["mo"] += mo_ok; cell["xu"] += xu_ok; cell["both"] += mo_ok and xu_ok
                if not (mo_ok and xu_ok) and cell["copied"] < CAP:
                    d = OUT / "v9combo_errors" / p0.parent.relative_to(SRC)
                    d.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(p0, d / f"gt-{gmo}-{gxu}__v9-{vmo}-{vxu}__{p0.name}")
                    cell["copied"] += 1
        agg = Counter()
        for (mo, cav), c in stats.items():
            if mo == mold:
                for k in ("n", "mo", "xu", "both"):
                    agg[k] += c[k]
        print(f"  ✔ {mold}: n={agg['n']}  雙對={agg['both']/max(1,agg['n'])*100:.1f}% "
              f"穴號={agg['xu']/max(1,agg['n'])*100:.1f}%  ({time.time()-t0:.0f}s)", flush=True)
        _dump(stats)

    _summary(stats)
    print(f"\n總 {total} 張，耗時 {time.time()-t0:.0f}s", flush=True)


def _dump(stats):
    obj = {f"{mo}/{cav}": {**{k: v for k, v in c.items() if k != 'xu_pred'},
                           "xu_pred": dict(c["xu_pred"].most_common())}
           for (mo, cav), c in stats.items()}
    (OUT / "_v9combo_raw.json").write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")


def _summary(stats):
    tot = Counter(); per = defaultdict(Counter); xuall = Counter()
    for (mo, cav), c in stats.items():
        for k in ("n", "mo", "xu", "both"):
            tot[k] += c[k]; per[mo][k] += c[k]
        xuall.update(c["xu_pred"])
    N = max(1, tot["n"])
    crnn = load_crnn()
    L = ["# v9混搭(v9.2模號+v9穴號) vs CRNN — 前處理區全量", ""]
    L.append(f"- 資料：`{SRC}` 全量 {tot['n']} 張原圖（p0+p90，2-pass）")
    L.append(f"- v9混搭：模號=v9.2(runs_v92) + 穴號=v9(runs/xuehao,18類無NG)")
    L.append(f"- CRNN：沿用既有 `_raw_stats.json`（未重跑）")
    L.append("")
    L.append("## 三方總體")
    ct = Counter()
    if crnn:
        for mo in MOLDS:
            for k in ("n", "crnn_mo", "crnn_xu", "crnn_both"):
                ct[k] += crnn[mo][k]
    cN = max(1, ct["n"])
    L.append("| 指標 | v9混搭 | v9.3(前測) | CRNN |")
    L.append("|---|---|---|---|")
    L.append(f"| 雙對率 | **{tot['both']/N*100:.2f}%** | 24.94% | {ct['crnn_both']/cN*100:.2f}% |")
    L.append(f"| 模號 | {tot['mo']/N*100:.2f}% | 99.80% | {ct['crnn_mo']/cN*100:.2f}% |")
    L.append(f"| 穴號 | **{tot['xu']/N*100:.2f}%** | 24.95% | {ct['crnn_xu']/cN*100:.2f}% |")
    L.append("")
    L.append(f"**v9 穴號預測分佈(前8)**：{xuall.most_common(8)}")
    L.append(f"　→ 04 佔比：**{xuall['04']/N*100:.1f}%**（v9.3 是 73.1%；看 v9 有無同樣吸收槽）")
    L.append("")
    L.append("## 每模號")
    L.append("| 模號 | N | v9混搭雙對 | v9混搭穴號 | CRNN雙對 | CRNN穴號 |")
    L.append("|---|---|---|---|---|---|")
    for mo in MOLDS:
        c = per[mo]; n = max(1, c["n"])
        cm = crnn[mo] if crnn else Counter(); cn = max(1, cm.get("n", 0))
        L.append(f"| {mo} | {c['n']} | {c['both']/n*100:.1f}% | {c['xu']/n*100:.1f}% | "
                 f"{cm.get('crnn_both',0)/cn*100:.1f}% | {cm.get('crnn_xu',0)/cn*100:.1f}% |")
    (OUT / "_v9combo_summary.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L))


if __name__ == "__main__":
    main()
