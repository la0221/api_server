# -*- coding: utf-8 -*-
"""Golden test 對照端：用 Python engine（同一份 ONNX）對 cs_golden.json 列出的同一批檔案
逐張推論，輸出 py_golden.json，並與 C# 結果逐張比對（label 必須一致、conf 容差內）。

用法：
  python golden_dump.py [cs_golden.json] [mohao.onnx] [xuehao.onnx] [py_golden.json]
"""
import json
import sys
from pathlib import Path

import cv2
import numpy as np

# ── 接 G:\隱眼專案\app 的 engine（與訓練/部署同一套前處理）──
APP_DIR = r"G:\隱眼專案\app"
sys.path.insert(0, APP_DIR)
from ocr.engine import OcrEngine  # noqa: E402

CS_JSON = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name("cs_golden.json")
MOHAO = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(r"G:\隱眼專案\weights\mohao\best.onnx")
XUEHAO = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(r"G:\隱眼專案\weights\xuehao\weights\best.onnx")
PY_JSON = Path(sys.argv[4]) if len(sys.argv) > 4 else Path(__file__).with_name("py_golden.json")

CONF_TOL = 0.02  # 信心容差（cv2 vs OpenCvSharp warpPolar 插值微差）


def imread_unicode(path):
    arr = np.fromfile(path, dtype=np.uint8)
    return cv2.imdecode(arr, cv2.IMREAD_COLOR)


def main():
    cs = json.loads(CS_JSON.read_text(encoding="utf-8"))
    files = list(cs.keys())
    print(f"[py] files from cs_golden.json: {len(files)}")
    print(f"[py] mohao = {MOHAO}")
    print(f"[py] xuehao= {XUEHAO}")

    # annulus 兩 head、passes=2（與 C# 端 WarpPolarParams 預設 + passes:2 一致）
    engine = OcrEngine(MOHAO, XUEHAO, mohao_pre="annulus", xuehao_pre="annulus",
                       passes=2, early_exit_conf=None)

    py = {}
    for i, f in enumerate(files):
        im = imread_unicode(f)
        if im is None:
            py[f] = {"present": False, "mohao": "", "conf_m": 0.0, "xuehao": "", "conf_x": 0.0}
            continue
        r = engine.predict(im, apply_roi=False)  # 已是判定區域，不再裁 IDS_ROI
        py[f] = {
            "present": bool(r.present),
            "mohao": r.mohao, "conf_m": round(float(r.conf_mohao), 6),
            "xuehao": r.xuehao, "conf_x": round(float(r.conf_xuehao), 6),
        }
        if (i + 1) % 100 == 0:
            print(f"[py] {i+1}/{len(files)}")

    PY_JSON.write_text(json.dumps(py, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[py] wrote {PY_JSON}")

    # ── 比對 ──
    n = 0
    mo_mismatch, xu_mismatch, present_mismatch = [], [], []
    conf_m_max, conf_x_max = 0.0, 0.0
    for f, c in cs.items():
        p = py.get(f)
        if p is None:
            continue
        n += 1
        if bool(c["present"]) != bool(p["present"]):
            present_mismatch.append(f)
        if c["mohao"] != p["mohao"]:
            mo_mismatch.append((f, c["mohao"], c["conf_m"], p["mohao"], p["conf_m"]))
        if c["xuehao"] != p["xuehao"]:
            xu_mismatch.append((f, c["xuehao"], c["conf_x"], p["xuehao"], p["conf_x"]))
        conf_m_max = max(conf_m_max, abs(c["conf_m"] - p["conf_m"]))
        conf_x_max = max(conf_x_max, abs(c["conf_x"] - p["conf_x"]))

    print("\n==================== GOLDEN COMPARE ====================")
    print(f" compared           : {n}")
    print(f" present mismatch    : {len(present_mismatch)}")
    print(f" mohao  label diff   : {len(mo_mismatch)}")
    print(f" xuehao label diff   : {len(xu_mismatch)}")
    print(f" max |conf_m| diff   : {conf_m_max:.4f}")
    print(f" max |conf_x| diff   : {conf_x_max:.4f}")

    def show(title, items):
        if not items:
            return
        print(f"\n -- {title} (前 15) --")
        for it in items[:15]:
            print("   ", it)

    show("mohao 不一致", mo_mismatch)
    show("xuehao 不一致", xu_mismatch)
    show("present 不一致", present_mismatch)

    label_ok = (not mo_mismatch) and (not xu_mismatch) and (not present_mismatch)
    conf_ok = (conf_m_max <= CONF_TOL) and (conf_x_max <= CONF_TOL)
    print("\n RESULT:",
          "PASS ✅" if (label_ok and conf_ok)
          else ("LABEL-OK, conf 超容差 ⚠️" if label_ok else "FAIL ❌"))
    return 0 if (label_ok and conf_ok) else 1


if __name__ == "__main__":
    sys.exit(main())
