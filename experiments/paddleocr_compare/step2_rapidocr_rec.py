# -*- coding: utf-8 -*-
r"""
⑤ PaddleOCR 開源對照試——B 段：用 RapidOCR（PaddleOCR PP-OCR 模型的 ONNX 發行版，Apache-2.0）
對 A 段產出的 200×80 crops 做「辨識（rec）」，與資料夾正解比對。

- 零訓練 zero-shot：探「通用字元式 OCR 在我們資料上的下限」。輸入與 CRNN 完全同源
  （同前處理、同 detector 裁窗）→ 比的是辨識器本身。
- 在獨立 venv 跑（只裝 rapidocr-onnxruntime），與 sidecar 的 torch 環境零相依。

用法（venv python）：python step2_rapidocr_rec.py [--limit N]
輸出：results.jsonl + 終端摘要（模號/穴號/雙軸準確率、延遲、混淆 top10）
"""
import argparse
import collections
import json
import re
import time
from pathlib import Path

from rapidocr_onnxruntime import RapidOCR

NORM = re.compile(r"[^A-Z0-9]")


def norm(s: str) -> str:
    return NORM.sub("", (s or "").upper())


def match(read: str, truth: str) -> bool:
    r, t = norm(read), norm(truth)
    if r == t:
        return True
    # 穴號 "08" vs "8"：去前導零再比一次
    return r.lstrip("0") == t.lstrip("0") != ""


def rec_one(engine, path: str):
    t0 = time.perf_counter()
    result, _ = engine(path, use_det=False, use_cls=True, use_rec=True)
    ms = (time.perf_counter() - t0) * 1000
    if not result:
        return "", 0.0, ms
    # 實測 rec-only 回傳列 = [框/佔位, 文字, 分數]（取尾兩欄最穩）；多列取分數最高。
    best = max(result, key=lambda x: float(x[-1]))
    return str(best[-2]), float(best[-1]), ms


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="最多處理筆數（0=全部）")
    args = ap.parse_args()

    here = Path(__file__).parent
    rows = [json.loads(l) for l in open(here / "manifest.jsonl", encoding="utf-8")]
    if args.limit > 0:
        rows = rows[: args.limit]

    engine = RapidOCR()
    out = open(here / "results.jsonl", "w", encoding="utf-8")
    n = m_ok = x_ok = both_ok = 0
    times = []
    confuse = collections.Counter()

    for row in rows:
        m_text, m_conf, m_ms = rec_one(engine, row["m_crop"])
        x_text, x_conf, x_ms = rec_one(engine, row["x_crop"])
        times += [m_ms, x_ms]
        mo = match(m_text, row["mohao_truth"])
        xo = match(x_text, row["xuehao_truth"])
        n += 1
        m_ok += mo
        x_ok += xo
        both_ok += (mo and xo)
        if not mo:
            confuse[f"模號 {row['mohao_truth']}→{norm(m_text) or '(空)'}"] += 1
        if not xo:
            confuse[f"穴號 {row['xuehao_truth']}→{norm(x_text) or '(空)'}"] += 1
        out.write(json.dumps({
            **{k: row[k] for k in ("mohao_truth", "xuehao_truth", "src")},
            "m_read": m_text, "m_conf": round(m_conf, 4),
            "x_read": x_text, "x_conf": round(x_conf, 4),
            "m_ok": mo, "x_ok": xo,
        }, ensure_ascii=False) + "\n")
        if n % 50 == 0:
            print(f"  ... {n}/{len(rows)}")

    out.close()
    times.sort()
    p50 = times[len(times) // 2] if times else 0
    print(f"\n===== RapidOCR(PP-OCR) zero-shot 對照結果 =====")
    print(f"樣本 {n} 對（模號+穴號 crops 各 {n}）")
    print(f"模號正確  {m_ok}/{n}  ({m_ok/n:.2%})")
    print(f"穴號正確  {x_ok}/{n}  ({x_ok/n:.2%})")
    print(f"雙軸皆對  {both_ok}/{n}  ({both_ok/n:.2%})")
    print(f"單 crop 辨識 p50 {p50:.0f}ms (CPU)")
    print(f"\n混淆 top10：")
    for k, v in confuse.most_common(10):
        print(f"  {k} ×{v}")


if __name__ == "__main__":
    main()
