"""Parse D:/incoming/v9.3vsrcnn/crnn_errors/**/*.png filenames into manifest + summary.

Filename format: gt-{MOHAO}-{XX}__crnn-{PRED_MOHAO}-{YY}__...
Special: crnn-NA-NA = detector miss.
Also: crnn-{Mohao}-{M?} = xuehao read as 'M' char (position collapse).
"""
from __future__ import annotations
import csv, re, sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(r"D:/incoming/v9.3vsrcnn/crnn_errors")
OUT_DIR = Path(r"D:/Content_lens_OCR/.ai/records/2026-07/2026-07-27")
OUT_DIR.mkdir(parents=True, exist_ok=True)

# gt-M17-06__crnn-M17-04__M17-06_...
# gt-M17-17__crnn-NA-NA__...
# gt-M101-15__crnn-M101-M5__...
RE_NAME = re.compile(
    r"^gt-(?P<gt_mo>[A-Z0-9]+)-(?P<gt_xu>[A-Z0-9]+)"
    r"__crnn-(?P<pred_mo>[A-Z0-9]+)-(?P<pred_xu>[A-Z0-9]+)"
    r"__.+\.png$"
)


def classify(gt_mo: str, gt_xu: str, pred_mo: str, pred_xu: str) -> str:
    if pred_mo == "NA" and pred_xu == "NA":
        return "detector_miss"
    mo_bad = gt_mo != pred_mo
    xu_bad = gt_xu != pred_xu
    if mo_bad and xu_bad:
        return "both_misread"
    if mo_bad:
        return "mohao_misread"
    if xu_bad:
        return "xuehao_misread"
    return "unknown_equal"  # shouldn't happen


rows: list[dict] = []
skipped: list[str] = []

for p in ROOT.rglob("*.png"):
    m = RE_NAME.match(p.name)
    if not m:
        skipped.append(str(p))
        continue
    gd = m.groupdict()
    mold = gd["gt_mo"]  # sub-folder name is redundant; use gt_mo
    err = classify(gd["gt_mo"], gd["gt_xu"], gd["pred_mo"], gd["pred_xu"])
    rows.append(
        dict(
            file=str(p.relative_to(ROOT)).replace("\\", "/"),
            mold=mold,
            gt_mo=gd["gt_mo"],
            gt_xu=gd["gt_xu"],
            pred_mo=gd["pred_mo"],
            pred_xu=gd["pred_xu"],
            gt_full=f"{gd['gt_mo']}-{gd['gt_xu']}",
            pred_full=f"{gd['pred_mo']}-{gd['pred_xu']}",
            err_type=err,
        )
    )

# ---- write manifest CSV
csv_path = OUT_DIR / "crnn_errors_manifest.csv"
with csv_path.open("w", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(
        f,
        fieldnames=["file", "mold", "gt_mo", "gt_xu", "pred_mo", "pred_xu",
                    "gt_full", "pred_full", "err_type"],
    )
    w.writeheader()
    w.writerows(rows)

# ---- summary
total = len(rows)
by_mold = Counter(r["mold"] for r in rows)
by_type = Counter(r["err_type"] for r in rows)
by_mold_type: dict[tuple[str, str], int] = defaultdict(int)
for r in rows:
    by_mold_type[(r["mold"], r["err_type"])] += 1
by_confusion = Counter((r["gt_full"], r["pred_full"]) for r in rows if r["err_type"] != "detector_miss")

# per-mold, per-xuehao gt coverage (how many real crops per (mold, xuehao) exist)
by_mold_xu = defaultdict(int)
for r in rows:
    by_mold_xu[(r["mold"], r["gt_xu"])] += 1

# per-mold, per-mohao if mohao misread
mohao_confusion_by_mold: dict[str, Counter] = defaultdict(Counter)
for r in rows:
    if r["err_type"] in ("mohao_misread", "both_misread"):
        mohao_confusion_by_mold[r["mold"]][(r["gt_mo"], r["pred_mo"])] += 1

md = []
md.append(f"# CRNN Errors Manifest — v9.3 (2026-07-27)\n")
md.append(f"Source: `D:/incoming/v9.3vsrcnn/crnn_errors/`\n")
md.append(f"Total: **{total}** errors (skipped: {len(skipped)})\n")

md.append("\n## By mold\n")
md.append("| mold | count |\n|---|---|\n")
for m_, c in by_mold.most_common():
    md.append(f"| {m_} | {c} |\n")

md.append("\n## By error type\n")
md.append("| type | count |\n|---|---|\n")
for t, c in by_type.most_common():
    md.append(f"| {t} | {c} |\n")

md.append("\n## By (mold × type)\n")
md.append("| mold | detect_miss | mohao_misread | xuehao_misread | both_misread |\n|---|---|---|---|---|\n")
mold_order = [m for m, _ in by_mold.most_common()]
for m_ in mold_order:
    md.append(
        f"| {m_} | {by_mold_type[(m_,'detector_miss')]} | "
        f"{by_mold_type[(m_,'mohao_misread')]} | "
        f"{by_mold_type[(m_,'xuehao_misread')]} | "
        f"{by_mold_type[(m_,'both_misread')]} |\n"
    )

md.append("\n## Top-20 confusion (gt → pred)\n")
md.append("| gt | pred | count |\n|---|---|---|\n")
for (gt, pr), c in by_confusion.most_common(20):
    md.append(f"| {gt} | {pr} | {c} |\n")

md.append("\n## Mohao confusion per mold\n")
for m_ in mold_order:
    conf = mohao_confusion_by_mold[m_]
    if not conf:
        continue
    md.append(f"\n### {m_}\n")
    md.append("| gt_mohao | pred_mohao | count |\n|---|---|---|\n")
    for (gm, pm), c in conf.most_common():
        md.append(f"| {gm} | {pm} | {c} |\n")

md.append("\n## Per (mold, gt_xuehao) coverage\n")
md.append("Number of real error crops available per xuehao — rehearsal budget (target 30-50/xuehao/mold per HANDOFF).\n\n")
mold_xu_by_mold: dict[str, list[tuple[str, int]]] = defaultdict(list)
for (m_, xu), c in by_mold_xu.items():
    mold_xu_by_mold[m_].append((xu, c))
for m_ in mold_order:
    xs = sorted(mold_xu_by_mold[m_], key=lambda t: -t[1])
    md.append(f"\n### {m_}\n")
    md.append("| xuehao | count | ≥30? |\n|---|---|---|\n")
    for xu, c in xs:
        ok = "✓" if c >= 30 else ("~" if c >= 15 else "✗")
        md.append(f"| {xu} | {c} | {ok} |\n")

md.append("\n## Skipped (unrecognized filename)\n")
if skipped:
    for s in skipped[:20]:
        md.append(f"- `{s}`\n")
    if len(skipped) > 20:
        md.append(f"- ... +{len(skipped)-20} more\n")
else:
    md.append("(none)\n")

md_path = OUT_DIR / "crnn_errors_summary.md"
md_path.write_text("".join(md), encoding="utf-8")

print(f"manifest: {csv_path}  ({total} rows)")
print(f"summary : {md_path}")
print(f"skipped : {len(skipped)}")
