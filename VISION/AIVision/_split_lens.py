"""
切分 隱眼專案:每個 模號/穴號 葉夾 → 前 10 張(依時間/檔名)訓練、其餘驗證。
複製(不移動),輸出 訓練用/ 與 驗證用/,保留 <模號>/<穴號>/ 結構。
"""
import re, shutil, json
from pathlib import Path

SRC = Path(r"D:\Toro_Project\VISION\AIVision\隱眼專案")
TRAIN = SRC / "訓練用"
VAL = SRC / "驗證用"
N_TRAIN = 10
EXTS = (".jpg", ".png", ".bmp")

for d in (TRAIN, VAL):
    if d.exists():
        shutil.rmtree(d)
    d.mkdir(parents=True)

molds = sorted([d for d in SRC.iterdir() if d.is_dir() and re.match(r"^M\d+$", d.name)],
               key=lambda p: p.name)

summary = {}
tot_tr = tot_va = 0
warn = []
for mold in molds:
    m_tr = m_va = 0
    for cav in sorted([c for c in mold.iterdir() if c.is_dir()], key=lambda p: p.name):
        imgs = sorted([f for f in cav.iterdir() if f.suffix.lower() in EXTS], key=lambda p: p.name)
        if len(imgs) < N_TRAIN:
            warn.append(f"{mold.name}/{cav.name} 只有 {len(imgs)} 張(<{N_TRAIN})")
        train, val = imgs[:N_TRAIN], imgs[N_TRAIN:]
        for f in train:
            dst = TRAIN / mold.name / cav.name / f.name
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dst)
        for f in val:
            dst = VAL / mold.name / cav.name / f.name
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dst)
        m_tr += len(train); m_va += len(val)
    summary[mold.name] = {"train": m_tr, "val": m_va, "cavities": len(list(mold.iterdir()))}
    tot_tr += m_tr; tot_va += m_va

report = {"molds": len(molds), "train_total": tot_tr, "val_total": tot_va,
          "per_mold": summary, "warnings": warn}
(SRC / "_split_summary.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

print(f"模號數={len(molds)}  訓練總計={tot_tr}  驗證總計={tot_va}")
print("每模號 [train/val]:")
for m, v in summary.items():
    print(f"  {m}: {v['train']}/{v['val']}")
if warn:
    print("⚠️ 警告:")
    for w in warn:
        print("  ", w)
else:
    print("無 <10 張的葉夾")
