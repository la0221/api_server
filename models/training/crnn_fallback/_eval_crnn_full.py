# -*- coding: utf-8 -*-
"""CRNN 獨立完整 benchmark：前處理區全量，模號+穴號雙對、偵測失敗率、穴號預測分佈。
  單 pass p0(比照部署)。GT 取檔名。
Run: & lens-gpu python _eval_crnn_full.py --device 0
"""
from __future__ import annotations
import argparse, json, re, sys, time
from collections import Counter, defaultdict
from pathlib import Path
import numpy as np, torch

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, "D:/OCR_demo/models/crnn")
from v6_preprocess import imread_unicode          # noqa
from crnn_dataset import crop_band, to_tensor       # noqa
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES  # noqa
from ultralytics import YOLO

SRC = Path("D:/模號穴號-穩定圖片區/前處理區")
OUT = Path("D:/tmp/crnn_bench")
MOLDS = ["M101", "M17", "M28", "M54", "M83"]
KEY = re.compile(r"(M\d+)-(\d+)")
HALF, DET_CONF, BIMG = 100, 0.25, 32


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--device", default="0")
    args = ap.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    det = YOLO("D:/OCR_demo/models/crnn/runs/detector/weights/best.pt")
    ck = torch.load("D:/OCR_demo/models/crnn/runs/nonar_include_M54/best.pt",
                    map_location="cuda:0", weights_only=False)
    ocr = NonAROCR(num_classes=NUM_CLASSES).to("cuda:0").eval(); ocr.load_state_dict(ck["model"])
    print(f"[CRNN bench] {SRC}", flush=True)

    def read_batch(strips):
        res = [("?", "?")] * len(strips)
        dets = det.predict(strips, conf=DET_CONF, verbose=False, device=0)
        crops = []; tens = []
        for k, (st, r) in enumerate(zip(strips, dets)):
            if r.boxes is None or len(r.boxes) == 0:
                continue
            cl = r.boxes.cls.cpu().numpy().astype(int); cf = r.boxes.conf.cpu().numpy(); xy = r.boxes.xywh.cpu().numpy()
            def ctr(cid):
                idx = np.where(cl == cid)[0]
                return None if len(idx) == 0 else int(round(xy[idx[np.argmax(cf[idx])]][0]))
            mcx, xcx = ctr(0), ctr(1)
            if mcx is None or xcx is None:
                continue
            band = crop_band(st); W = band.shape[1]
            crops.append((k, 0)); tens.append(to_tensor(band[:, np.arange(mcx-HALF, mcx+HALF) % W]))
            crops.append((k, 1)); tens.append(to_tensor(band[:, np.arange(xcx-HALF, xcx+HALF) % W]))
        if tens:
            with torch.no_grad():
                ids = ocr(torch.stack(tens, 0).to("cuda:0")).argmax(-1).cpu().numpy()
            tmp = defaultdict(lambda: ["?", "?"])
            for (k, wh), row in zip(crops, ids):
                tmp[k][wh] = decode_padded(row)
            for k, mx in tmp.items():
                res[k] = (mx[0], mx[1])
        return res

    per = defaultdict(lambda: {"n": 0, "mo": 0, "xu": 0, "both": 0, "detfail": 0})
    xu_pred = Counter(); t0 = time.time(); N = 0
    for mold in MOLDS:
        p0s = sorted((SRC / mold).rglob("*_p0.png"))
        for bi in range(0, len(p0s), BIMG):
            batch = p0s[bi:bi+BIMG]; metas = []
            for p0 in batch:
                m = KEY.search(p0.name)
                if not m: continue
                s0 = imread_unicode(p0)
                if s0 is None: continue
                metas.append((m.group(1), m.group(2), s0))
            if not metas: continue
            reads = read_batch([x[2] for x in metas])
            for j, (gmo, gxu, _) in enumerate(metas):
                mo, xu = reads[j]; c = per[mold]; c["n"] += 1; N += 1
                xu_pred[xu] += 1
                if mo == "?" or xu == "?": c["detfail"] += 1
                mo_ok, xu_ok = mo == gmo, xu == gxu
                c["mo"] += mo_ok; c["xu"] += xu_ok; c["both"] += mo_ok and xu_ok
        a = per[mold]
        print(f"  ✔ {mold}: n={a['n']} 雙對={a['both']/max(1,a['n'])*100:.1f}% "
              f"模號={a['mo']/max(1,a['n'])*100:.1f}% 穴號={a['xu']/max(1,a['n'])*100:.1f}% "
              f"detfail={a['detfail']} ({time.time()-t0:.0f}s)", flush=True)

    tot = Counter()
    for c in per.values():
        for k in ("n", "mo", "xu", "both", "detfail"): tot[k] += c[k]
    n = max(1, tot["n"])
    L = ["# CRNN 獨立完整 benchmark — 前處理區全量", ""]
    L.append(f"- N={tot['n']}  單 pass p0(比照部署)")
    L.append("| 指標 | CRNN | (參考)V9.4 |")
    L.append("|---|---|---|")
    L.append(f"| 雙對 | **{tot['both']/n*100:.2f}%** | ~99.3% |")
    L.append(f"| 模號 | {tot['mo']/n*100:.2f}% | 99.80% |")
    L.append(f"| 穴號 | {tot['xu']/n*100:.2f}% | ~99.5% |")
    L.append(f"| 偵測失敗 | {tot['detfail']}（{tot['detfail']/n*100:.2f}%） | — |")
    L.append("")
    L.append(f"穴號預測分佈(前6，吸收槽檢查): {xu_pred.most_common(6)}")
    L.append("")
    L.append("## 每模號")
    L.append("| 模號 | N | 雙對 | 模號 | 穴號 | detfail |")
    L.append("|---|---|---|---|---|---|")
    for mo in MOLDS:
        c = per[mo]; nn = max(1, c["n"])
        L.append(f"| {mo} | {c['n']} | {c['both']/nn*100:.1f}% | {c['mo']/nn*100:.1f}% | "
                 f"{c['xu']/nn*100:.1f}% | {c['detfail']} |")
    (OUT / "_crnn_bench.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L)); print(f"\n耗時 {time.time()-t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
