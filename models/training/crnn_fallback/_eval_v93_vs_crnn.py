# -*- coding: utf-8 -*-
"""v9.3(分類) vs CRNN(讀字) — 前處理區全量、雙對(模號+穴號)對比。

輸入：D:\\模號穴號-穩定圖片區\\前處理區\\<模號>\\<收圖包>\\<穴號>\\<模號>-<穴號>_..._p{0,90}.png
  單位＝每張原圖（一組 p0+p90）。GT 取自檔名。
兩模型皆吃已前處理好的 640×640 annulus strip（兩者 annulus_polar 相同，見 crnn_dataset）：
  v9.3 ：模號 head + 穴號 head，各自 2-pass(p0,p90) 取最高信心（比照部署）。
  CRNN ：detector 找模號/穴號框 → crop_band 環狀 wrap 裁 200×80 → Non-AR 讀字（單 pass p0，比照部署）。
指標：雙對率(模號&穴號都對) / 模號準確率 / 穴號準確率。
錯誤集：每模型「非雙對」者，p0 strip 複製到 D:\\tmp\\v9.3vsrcnn\\{v9.3,crnn}_errors\\<鏡像>\\
  （每 (模號,真穴號) 上限 CAP 張，避免爆量；完整計數在 summary）。

Run: & lens-gpu python _eval_v93_vs_crnn.py --device 0
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

import cv2
import numpy as np
import torch

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "OCR" / "yolo_a_V6"))
sys.path.insert(0, str(HERE))
sys.path.insert(0, "D:/OCR_demo")
from v6_preprocess import imread_unicode          # noqa
from crnn_dataset import crop_band, to_tensor       # noqa
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES  # noqa
from app.config import weights_for                  # noqa

SRC = Path("D:/模號穴號-穩定圖片區/前處理區")
OUT = Path("D:/tmp/v9.3vsrcnn")
MOLDS = ["M101", "M17", "M28", "M54", "M83"]
KEY = re.compile(r"(M\d+)-(\d+)")
HALF_W = 100
DET_CONF = 0.25
BIMG = 24          # 每批原圖數
CAP = 25           # 每 (模號,真穴號) 每模型錯誤圖複製上限


def load_models(dev):
    from ultralytics import YOLO
    mo_w, xu_w = weights_for("V9.3")
    clf_mo, clf_xu = YOLO(str(mo_w)), YOLO(str(xu_w))
    det = YOLO(str(HERE / "runs/detector/weights/best.pt"))
    ck = torch.load(HERE / "runs/nonar_include_M54/best.pt", map_location=dev, weights_only=False)
    ocr = NonAROCR(num_classes=NUM_CLASSES).to(dev).eval()
    ocr.load_state_dict(ck["model"])
    return clf_mo, clf_xu, det, ocr


def v93_batch(clf, strips):
    """回傳每張 (label, conf)。"""
    out = []
    for r in clf.predict(strips, imgsz=640, verbose=False, device=0):
        p = r.probs.data.cpu().numpy(); i = int(p.argmax())
        out.append((clf.names[i], float(p[i])))
    return out


def crnn_batch(det, ocr, strips, dev):
    """對一批 p0 strip：detector→crop→OCR，回傳每張 (mo_str, xu_str)。"""
    res = [("?", "?")] * len(strips)
    dets = det.predict(strips, conf=DET_CONF, verbose=False, device=0)
    crops = []          # (img_idx, which) which: 0=mo 1=xu
    tensors = []
    for k, (strip, r) in enumerate(zip(strips, dets)):
        if r.boxes is None or len(r.boxes) == 0:
            continue
        cls = r.boxes.cls.cpu().numpy().astype(int)
        conf = r.boxes.conf.cpu().numpy()
        xywh = r.boxes.xywh.cpu().numpy()

        def center(cid):
            idx = np.where(cls == cid)[0]
            return None if len(idx) == 0 else int(round(xywh[idx[np.argmax(conf[idx])]][0]))
        mcx, xcx = center(0), center(1)
        if mcx is None or xcx is None:
            continue
        band = crop_band(strip); W = band.shape[1]
        mc = band[:, np.arange(mcx - HALF_W, mcx + HALF_W) % W]
        xc = band[:, np.arange(xcx - HALF_W, xcx + HALF_W) % W]
        crops.append((k, 0)); tensors.append(to_tensor(mc))
        crops.append((k, 1)); tensors.append(to_tensor(xc))
    if tensors:
        xin = torch.stack(tensors, 0).to(dev)
        with torch.no_grad():
            ids = ocr(xin).argmax(-1).cpu().numpy()
        tmp = defaultdict(lambda: ["?", "?"])
        for (k, which), row in zip(crops, ids):
            tmp[k][which] = decode_padded(row)
        for k, mx in tmp.items():
            res[k] = (mx[0], mx[1])
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", default="0")
    ap.add_argument("--limit", type=int, default=0, help="每穴號抽樣上限(0=全量)")
    args = ap.parse_args()
    dev = "cuda:0"
    clf_mo, clf_xu, det, ocr = load_models(dev)
    OUT.mkdir(parents=True, exist_ok=True)
    print(f"[v9.3 vs CRNN] src={SRC}\n  out={OUT}", flush=True)

    # 統計容器：per (mold, cavity)
    def newcell():
        return {"n": 0,
                "v93_mo": 0, "v93_xu": 0, "v93_both": 0,
                "crnn_mo": 0, "crnn_xu": 0, "crnn_both": 0,
                "v93_xu_pred": Counter(), "crnn_xu_pred": Counter(),
                "crnn_fail": 0, "copied_v93": 0, "copied_crnn": 0}
    stats = defaultdict(newcell)
    t0 = time.time()
    total = 0

    for mold in MOLDS:
        base = SRC / mold
        if not base.exists():
            continue
        p0s = sorted(base.rglob("*_p0.png"))
        if args.limit:
            # 每穴號抽 limit：先分組
            byc = defaultdict(list)
            for p in p0s:
                m = KEY.search(p.name)
                if m:
                    byc[m.group(2)].append(p)
            p0s = [p for c in byc for p in byc[c][:args.limit]]
        for bi in range(0, len(p0s), BIMG):
            batch = p0s[bi:bi + BIMG]
            metas = []          # (p0, gt_mo, gt_xu, s0, s90)
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
            s0s = [x[3] for x in metas]
            s90s = [x[4] for x in metas]
            # v9.3 2-pass：對 p0+p90 各 head 各一次，再 max-conf
            mo0 = v93_batch(clf_mo, s0s); mo9 = v93_batch(clf_mo, s90s)
            xu0 = v93_batch(clf_xu, s0s); xu9 = v93_batch(clf_xu, s90s)
            # CRNN 單 pass p0
            crnn = crnn_batch(det, ocr, s0s, dev)

            for j, (p0, gmo, gxu, _, _) in enumerate(metas):
                cell = stats[(mold, gxu)]; cell["n"] += 1; total += 1
                vmo = mo0[j] if mo0[j][1] >= mo9[j][1] else mo9[j]
                vxu = xu0[j] if xu0[j][1] >= xu9[j][1] else xu9[j]
                vmo, vxu = vmo[0], vxu[0]
                cmo, cxu = crnn[j]
                cell["v93_xu_pred"][vxu] += 1
                cell["crnn_xu_pred"][cxu] += 1
                if cmo == "?" or cxu == "?":
                    cell["crnn_fail"] += 1
                v_mo_ok, v_xu_ok = vmo == gmo, vxu == gxu
                c_mo_ok, c_xu_ok = cmo == gmo, cxu == gxu
                cell["v93_mo"] += v_mo_ok; cell["v93_xu"] += v_xu_ok
                cell["v93_both"] += v_mo_ok and v_xu_ok
                cell["crnn_mo"] += c_mo_ok; cell["crnn_xu"] += c_xu_ok
                cell["crnn_both"] += c_mo_ok and c_xu_ok
                # 錯誤集複製（非雙對）；? 是 Windows 非法檔名字元 → 換 NA
                def sn(s):
                    return s.replace("?", "NA")
                rel = p0.parent.relative_to(SRC)
                if not (v_mo_ok and v_xu_ok) and cell["copied_v93"] < CAP:
                    d = OUT / "v9.3_errors" / rel; d.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(p0, d / f"gt-{gmo}-{gxu}__v93-{sn(vmo)}-{sn(vxu)}__{p0.name}")
                    cell["copied_v93"] += 1
                if not (c_mo_ok and c_xu_ok) and cell["copied_crnn"] < CAP:
                    d = OUT / "crnn_errors" / rel; d.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(p0, d / f"gt-{gmo}-{gxu}__crnn-{sn(cmo)}-{sn(cxu)}__{p0.name}")
                    cell["copied_crnn"] += 1
        # per-mold 進度
        mm = _agg(stats, mold)
        print(f"  ✔ {mold}: n={mm['n']}  雙對 v9.3={mm['v93_both']/max(1,mm['n'])*100:.1f}% "
              f"CRNN={mm['crnn_both']/max(1,mm['n'])*100:.1f}%  ({time.time()-t0:.0f}s)", flush=True)
        _dump(stats)

    _dump(stats)
    _summary(stats)
    print(f"\n總 {total} 張原圖，耗時 {time.time()-t0:.0f}s", flush=True)


def _agg(stats, mold):
    a = Counter()
    for (mo, cav), c in stats.items():
        if mo != mold:
            continue
        for k in ("n", "v93_mo", "v93_xu", "v93_both", "crnn_mo", "crnn_xu", "crnn_both", "crnn_fail"):
            a[k] += c[k]
    return a


def _dump(stats):
    obj = {}
    for (mo, cav), c in stats.items():
        obj[f"{mo}/{cav}"] = {k: (dict(v.most_common()) if isinstance(v, Counter) else v)
                              for k, v in c.items()}
    (OUT / "_raw_stats.json").write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")


def _summary(stats):
    tot = Counter()
    per_mold = defaultdict(Counter)
    for (mo, cav), c in stats.items():
        for k in ("n", "v93_mo", "v93_xu", "v93_both", "crnn_mo", "crnn_xu", "crnn_both", "crnn_fail"):
            tot[k] += c[k]; per_mold[mo][k] += c[k]
    N = max(1, tot["n"])
    L = ["# v9.3(分類) vs CRNN(讀字) — 前處理區全量對比", ""]
    L.append(f"- 資料：`{SRC}`，全量 {tot['n']} 張原圖（每張 p0+p90）")
    L.append(f"- v9.3：模號+穴號 head，各 2-pass 取最高信心（比照部署）")
    L.append(f"- CRNN：detector+Non-AR 讀字，單 pass p0（比照部署）")
    L.append(f"- GT：檔名 `M<模號>-<穴號>`；雙對＝模號&穴號都對")
    L.append("")
    L.append("## 總體")
    L.append("| 指標 | v9.3 | CRNN |")
    L.append("|---|---|---|")
    L.append(f"| **雙對率** | **{tot['v93_both']/N*100:.2f}%** | **{tot['crnn_both']/N*100:.2f}%** |")
    L.append(f"| 模號準確 | {tot['v93_mo']/N*100:.2f}% | {tot['crnn_mo']/N*100:.2f}% |")
    L.append(f"| 穴號準確 | {tot['v93_xu']/N*100:.2f}% | {tot['crnn_xu']/N*100:.2f}% |")
    L.append(f"| CRNN 偵測失敗 |  | {tot['crnn_fail']}（{tot['crnn_fail']/N*100:.2f}%） |")
    L.append("")
    L.append("## 每模號 雙對率")
    L.append("| 模號 | N | v9.3 雙對 | CRNN 雙對 | v9.3 模號 | v9.3 穴號 | CRNN 模號 | CRNN 穴號 |")
    L.append("|---|---|---|---|---|---|---|---|")
    for mo in MOLDS:
        c = per_mold[mo]; n = max(1, c["n"])
        L.append(f"| {mo} | {c['n']} | {c['v93_both']/n*100:.1f}% | {c['crnn_both']/n*100:.1f}% | "
                 f"{c['v93_mo']/n*100:.1f}% | {c['v93_xu']/n*100:.1f}% | "
                 f"{c['crnn_mo']/n*100:.1f}% | {c['crnn_xu']/n*100:.1f}% |")
    (OUT / "_summary.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L))


if __name__ == "__main__":
    main()
