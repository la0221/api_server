"""Evaluate nonar_v931_fix/best.pt on 前處理區 (untouched stable strips).

Input : D:/incoming/模號穴號-穩定圖片區/前處理區/{mold}/**/{gt_mo}-{gt_xu}_..._p{0|90}.png
Output: D:/incoming/crnn結果/
  - report.md
  - summary.csv (per pair)
  - errors/{mold}/gt_{XX}__pred_{YY}/*.png  (copies of wrong strip pairs)
  - errors_detail.csv (per-error detail)
  - detect_miss/{mold}/*.png (strips where either head is ?)

Uses 2-pass: pair up p0 + p90 with matching stem, per-head keep higher-conf.
Also flags strips overlapping with training rehearsal set.
"""
from __future__ import annotations

import csv
import re
import shutil
import sys
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np
import torch

REPO = Path(r"D:/Content_lens_OCR")
sys.path.insert(0, str(REPO / "OCR" / "crnn_fallback"))
from crnn_dataset import crop_band, to_tensor  # noqa: E402
from nonar_model import NonAROCR, decode_padded, BLANK_ID, NUM_CLASSES, N_QUERIES  # noqa: E402
from ultralytics import YOLO  # noqa: E402

import argparse
STABLE_ROOT = Path(r"D:/incoming/模號穴號-穩定圖片區/前處理區")
DEFAULT_OUT_ROOT = Path(r"D:/incoming/crnn結果")
DET_WEIGHTS = REPO / "OCR/crnn_fallback/runs/detector/weights/best.pt"
DEFAULT_NONAR_WEIGHTS = REPO / "OCR/crnn_fallback/runs/nonar_v931_fix/best.pt"
TRAIN_STABLE = REPO / "data_stable_crops" / "train"
HALF_W = 100
DET_CONF = 0.10  # lower than production 0.25, per today's finding (rescues 72/90 detect-misses)

# filename: {gt_mo}-{gt_xu}_..._p{0|90}.png
RE_NAME = re.compile(
    r"^(?P<gt_mo>M[0-9]+)-(?P<gt_xu>[0-9]+)_(?P<rest>.+)_p(?P<rot>0|90)\.png$"
)


def imread_unicode(p: Path):
    data = np.fromfile(str(p), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_unicode(path: Path, img: np.ndarray) -> bool:
    ok, buf = cv2.imencode(".png", img)
    if not ok:
        return False
    buf.tofile(str(path))
    return True


def build_seen_stems() -> set[str]:
    """Extract stems of strips used for training so we can flag overlap."""
    seen = set()
    if not TRAIN_STABLE.exists():
        return seen
    for f in TRAIN_STABLE.rglob("*.png"):
        name = f.stem  # M83-01_M83-01_20260630134333_p0__err93_m
        if "__err93_" not in name:
            continue
        stem_part = name.split("__err93_")[0]  # M83-01_M83-01_20260630134333_p0
        # remove leading "MXX-YY_" (the gt label prefix we prepended)
        parts = stem_part.split("_", 1)
        if len(parts) == 2:
            seen.add(parts[1])  # M83-01_20260630134333_p0
    return seen


@torch.no_grad()
def infer_single(strip: np.ndarray, det, model, device):
    """Return (pred_m, conf_m, pred_x, conf_x). conf = det_conf * geo-mean ocr_conf."""
    pred_m, pred_x = "?", "?"
    conf_m, conf_x = 0.0, 0.0
    r = det.predict(strip, verbose=False, device=0, conf=DET_CONF)[0]
    if r.boxes is None or len(r.boxes) == 0:
        return pred_m, conf_m, pred_x, conf_x
    cls_arr = r.boxes.cls.cpu().numpy().astype(int)
    conf_arr = r.boxes.conf.cpu().numpy()
    xy_arr = r.boxes.xywh.cpu().numpy()
    band = crop_band(strip)
    W = band.shape[1]
    crops, heads, det_confs = [], [], []
    m_idx = np.where(cls_arr == 0)[0]
    x_idx = np.where(cls_arr == 1)[0]
    if len(m_idx) > 0:
        best = m_idx[int(np.argmax(conf_arr[m_idx]))]
        cx = int(round(float(xy_arr[best][0])))
        crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
        heads.append("m"); det_confs.append(float(conf_arr[best]))
    if len(x_idx) > 0:
        best = x_idx[int(np.argmax(conf_arr[x_idx]))]
        cx = int(round(float(xy_arr[best][0])))
        crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
        heads.append("x"); det_confs.append(float(conf_arr[best]))
    if not crops:
        return pred_m, conf_m, pred_x, conf_x
    xin = torch.stack([to_tensor(c) for c in crops], 0).to(device)
    probs = model(xin).softmax(-1).cpu().numpy()
    for i, h in enumerate(heads):
        pids = probs[i].argmax(-1)
        s = decode_padded(pids.tolist())
        confs = []
        for pos in range(N_QUERIES):
            tid = int(pids[pos])
            if tid == BLANK_ID:
                break
            confs.append(float(probs[i, pos, tid]))
        ocr_conf = float(np.prod(confs) ** (1.0 / max(len(confs), 1))) if confs else 0.0
        combined = det_confs[i] * ocr_conf
        if h == "m":
            pred_m, conf_m = s or "?", combined
        else:
            pred_x, conf_x = s or "?", combined
    return pred_m, conf_m, pred_x, conf_x


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default=str(DEFAULT_NONAR_WEIGHTS))
    ap.add_argument("--out", default=str(DEFAULT_OUT_ROOT))
    args = ap.parse_args()

    global NONAR_WEIGHTS, OUT_ROOT
    NONAR_WEIGHTS = Path(args.weights)
    OUT_ROOT = Path(args.out)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    ckpt = torch.load(NONAR_WEIGHTS, map_location=device, weights_only=False)
    model = NonAROCR(num_classes=NUM_CLASSES).to(device)
    model.load_state_dict(ckpt["model"])
    model.eval()
    print(f"[eval] weights={NONAR_WEIGHTS.name} ep={ckpt['epoch']} val={ckpt['val_acc']*100:.2f}%")
    print(f"[eval] det_conf={DET_CONF}")

    det = YOLO(str(DET_WEIGHTS))
    seen_stems = build_seen_stems()
    print(f"[data] training stems (for overlap flag): {len(seen_stems)}")

    OUT_ROOT.mkdir(parents=True, exist_ok=True)
    errors_dir = OUT_ROOT / "errors"
    detect_miss_dir = OUT_ROOT / "detect_miss"
    if errors_dir.exists():
        shutil.rmtree(errors_dir)
    if detect_miss_dir.exists():
        shutil.rmtree(detect_miss_dir)
    errors_dir.mkdir(parents=True, exist_ok=True)
    detect_miss_dir.mkdir(parents=True, exist_ok=True)

    # -- gather all strips grouped by (dir, stem_without_p) → {'0': path, '90': path}
    pairs: dict[tuple[str, str, str, str], dict[str, Path]] = {}
    unparsed = []
    for p in STABLE_ROOT.rglob("*.png"):
        m = RE_NAME.match(p.name)
        if not m:
            unparsed.append(str(p))
            continue
        gd = m.groupdict()
        key = (str(p.parent), gd["gt_mo"], gd["gt_xu"], gd["rest"])
        pairs.setdefault(key, {})[gd["rot"]] = p

    print(f"[data] parsed pairs: {len(pairs)}  (unparsed files: {len(unparsed)})")

    # -- process each pair
    rows = []
    err_detail = []
    total = 0
    correct_both = 0
    correct_mo_only = 0
    correct_xu_only = 0
    wrong_both = 0
    detect_miss_pairs = 0
    by_mold_total = Counter()
    by_mold_correct = Counter()
    by_mold_wrong = defaultdict(Counter)  # mold → Counter((gt,pred))

    overlap_seen = 0

    for i, ((dir_, gt_mo, gt_xu, rest), rots) in enumerate(pairs.items()):
        total += 1
        by_mold_total[gt_mo] += 1

        # infer each rotation, per-head keep higher conf
        best_m, best_m_c = "?", -1.0
        best_x, best_x_c = "?", -1.0
        rot_used_m, rot_used_x = None, None
        strip_paths = {}
        for rot, p in rots.items():
            strip_paths[rot] = p
            img = imread_unicode(p)
            if img is None or img.shape[0] != 640 or img.shape[1] != 640:
                continue
            pm, cm, px, cx = infer_single(img, det, model, device)
            if cm > best_m_c:
                best_m, best_m_c, rot_used_m = pm, cm, rot
            if cx > best_x_c:
                best_x, best_x_c, rot_used_x = px, cx, rot

        # overlap with training
        stem = f"{gt_mo}-{gt_xu}_{rest}_p0"
        was_seen = stem in seen_stems or f"{gt_mo}-{gt_xu}_{rest}_p90" in seen_stems
        if was_seen:
            overlap_seen += 1

        m_ok = best_m == gt_mo
        x_ok = best_x == gt_xu
        pred_full = f"{best_m}-{best_x}"
        gt_full = f"{gt_mo}-{gt_xu}"
        if m_ok and x_ok:
            correct_both += 1
            by_mold_correct[gt_mo] += 1
        elif m_ok:
            correct_mo_only += 1
        elif x_ok:
            correct_xu_only += 1
        else:
            wrong_both += 1

        if best_m == "?" or best_x == "?":
            detect_miss_pairs += 1
            # copy to detect_miss/{mold}/
            dst = detect_miss_dir / gt_mo
            dst.mkdir(parents=True, exist_ok=True)
            for rot, p in strip_paths.items():
                out = dst / p.name
                if not out.exists():
                    shutil.copy(p, out)

        row = dict(
            gt_mo=gt_mo, gt_xu=gt_xu,
            pred_mo=best_m, pred_xu=best_x,
            conf_mo=f"{best_m_c:.3f}", conf_xu=f"{best_x_c:.3f}",
            rot_m=rot_used_m or "", rot_x=rot_used_x or "",
            m_ok=int(m_ok), x_ok=int(x_ok),
            full_ok=int(m_ok and x_ok),
            was_in_training=int(was_seen),
            pair_dir=dir_,
            stem=rest,
        )
        rows.append(row)

        if not (m_ok and x_ok):
            by_mold_wrong[gt_mo][(gt_full, pred_full)] += 1
            # copy pair to errors/{mold}/gt_{XX}__pred_{YY}/
            sub = errors_dir / gt_mo / f"gt_{gt_full}__pred_{pred_full}"
            sub.mkdir(parents=True, exist_ok=True)
            for rot, p in strip_paths.items():
                out = sub / p.name
                if not out.exists():
                    shutil.copy(p, out)
            err_detail.append(dict(
                mold=gt_mo,
                gt=gt_full,
                pred=pred_full,
                m_ok=int(m_ok),
                x_ok=int(x_ok),
                conf_mo=f"{best_m_c:.3f}",
                conf_xu=f"{best_x_c:.3f}",
                strip_p0=str(strip_paths.get("0", "")),
                strip_p90=str(strip_paths.get("90", "")),
                was_in_training=int(was_seen),
            ))

        if (i + 1) % 200 == 0:
            print(f"  [{i+1}/{len(pairs)}] correct={correct_both} wrong_both={wrong_both} "
                  f"detect_miss={detect_miss_pairs}")

    # -- write summary CSV
    with (OUT_ROOT / "summary.csv").open("w", newline="", encoding="utf-8") as f:
        if rows:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
            w.writeheader(); w.writerows(rows)

    with (OUT_ROOT / "errors_detail.csv").open("w", newline="", encoding="utf-8") as f:
        if err_detail:
            w = csv.DictWriter(f, fieldnames=list(err_detail[0].keys()))
            w.writeheader(); w.writerows(err_detail)

    # -- report.md
    def pct(n, d):
        return f"{n}/{d} ({n/max(d,1)*100:.2f}%)"

    md = []
    md.append(f"# CRNN 效果測試報告 — nonar_v931_fix on 前處理區\n\n")
    md.append(f"**Weights**: `{NONAR_WEIGHTS}` (ep {ckpt['epoch']}, val {ckpt['val_acc']*100:.2f}%)\n")
    md.append(f"**Detector**: `{DET_WEIGHTS}` conf={DET_CONF}\n")
    md.append(f"**Source**: `{STABLE_ROOT}`\n\n")
    md.append(f"## Overall (per raw image, i.e. per p0/p90 pair)\n\n")
    md.append(f"| 指標 | 數字 |\n|---|---|\n")
    md.append(f"| Total pairs (raw images) | {total} |\n")
    md.append(f"| **Fully correct** | **{pct(correct_both, total)}** |\n")
    md.append(f"| Partial (mo only) | {pct(correct_mo_only, total)} |\n")
    md.append(f"| Partial (xu only) | {pct(correct_xu_only, total)} |\n")
    md.append(f"| Wrong (both) | {pct(wrong_both, total)} |\n")
    md.append(f"| Detect-miss (any '?') | {pct(detect_miss_pairs, total)} |\n")
    md.append(f"| Overlap w/ training (was in rehearsal) | {pct(overlap_seen, total)} |\n\n")

    md.append(f"## Per-mold accuracy\n\n")
    md.append(f"| mold | total | full_correct | acc% |\n|---|---|---|---|\n")
    for m in sorted(by_mold_total):
        t = by_mold_total[m]; c = by_mold_correct[m]
        md.append(f"| {m} | {t} | {c} | {c/max(t,1)*100:.2f}% |\n")

    md.append(f"\n## Errors by mold (top-20 confusions)\n")
    for m in sorted(by_mold_wrong):
        md.append(f"\n### {m}\n\n")
        md.append(f"| gt | pred | count |\n|---|---|---|\n")
        for (gt, pr), c in by_mold_wrong[m].most_common(20):
            md.append(f"| {gt} | {pr} | {c} |\n")

    md.append(f"\n## 產出檔案結構\n\n")
    md.append(f"```\n{OUT_ROOT}/\n")
    md.append(f"├── report.md            ← 本檔\n")
    md.append(f"├── summary.csv          ← 每對 raw 圖的推論結果\n")
    md.append(f"├── errors_detail.csv    ← 每個錯誤的詳細（gt/pred/信心/路徑）\n")
    md.append(f"├── errors/{{mold}}/gt_{{X}}__pred_{{Y}}/*.png   ← 錯誤 strip 對備份\n")
    md.append(f"└── detect_miss/{{mold}}/*.png                  ← detector 沒抓到框的 strip\n")
    md.append(f"```\n\n")

    md.append(f"## Notes\n\n")
    md.append(f"- 每對 raw 圖有兩個 strip（p0 和 p90），採 2-pass per-head 選信心高者\n")
    md.append(f"- Detector conf 用 0.10（比產線 0.25 低，今日測得可撈回 80% detect-miss）\n")
    md.append(f"- Overlap 標示：{overlap_seen} 對 raw 圖曾用於 rehearsal 訓練，其餘為完全 unseen\n")
    if unparsed:
        md.append(f"- {len(unparsed)} 個檔案 filename 解析失敗（見 unparsed_list.txt 若有）\n")

    (OUT_ROOT / "report.md").write_text("".join(md), encoding="utf-8")

    if unparsed:
        (OUT_ROOT / "unparsed_list.txt").write_text("\n".join(unparsed), encoding="utf-8")

    print()
    print("=" * 60)
    print(f"Total pairs: {total}")
    print(f"Full correct: {pct(correct_both, total)}")
    print(f"Wrong (both): {pct(wrong_both, total)}")
    print(f"Detect-miss: {pct(detect_miss_pairs, total)}")
    print(f"Overlap w/ training: {pct(overlap_seen, total)}")
    print(f"Report: {OUT_ROOT / 'report.md'}")


if __name__ == "__main__":
    main()
