"""探勘 隱眼專案 新資料集結構:模號×穴號、張數、命名、尺寸、多零件(時間群)、異常。"""
from pathlib import Path
import numpy as np, cv2, re
from collections import defaultdict

ROOT = Path(r"D:\Toro_Project\VISION\AIVision\隱眼專案")

print("=== 頂層 ===")
for p in sorted(ROOT.iterdir()):
    kind = "DIR " if p.is_dir() else "file"
    extra = ""
    if p.is_file():
        extra = f" ({p.stat().st_size/1e6:.1f} MB)"
    print(f"  [{kind}] {p.name}{extra}")

# 找所有含 jpg 的葉資料夾
leaf_counts = {}
all_jpgs = []
for d in ROOT.rglob("*"):
    if d.is_dir():
        jpgs = list(d.glob("*.jpg")) + list(d.glob("*.png")) + list(d.glob("*.bmp"))
        if jpgs:
            rel = d.relative_to(ROOT)
            leaf_counts[str(rel)] = len(jpgs)
            all_jpgs += jpgs

print(f"\n=== 含圖葉資料夾 {len(leaf_counts)} 個,總圖 {len(all_jpgs)} 張 ===")
for k in sorted(leaf_counts)[:80]:
    print(f"  {k}: {leaf_counts[k]}")
if len(leaf_counts) > 80:
    print(f"  ...(共 {len(leaf_counts)} 個葉夾)")

# 推斷層級:模號 / 穴號
print("\n=== 路徑層級樣本(前 8 個葉夾完整路徑)===")
for k in sorted(leaf_counts)[:8]:
    print("  ", k.replace("\\", " / "))

# 尺寸 + 命名 + 時間群(多零件判斷)
if all_jpgs:
    g = cv2.imdecode(np.fromfile(str(all_jpgs[0]), np.uint8), cv2.IMREAD_GRAYSCALE)
    print(f"\n=== 樣本 ===\n  尺寸: {g.shape}  檔名: {all_jpgs[0].name}")

# 對幾個葉夾看時間群(是否多個實體零件/多次取像)
print("\n=== 時間群分析(看是否多零件;檔名 *_HHMMSS_seq)===")
sample_leaves = sorted(leaf_counts, key=lambda k: -leaf_counts[k])[:6]
for lk in sample_leaves:
    d = ROOT / lk
    stamps = defaultdict(int)
    for f in (list(d.glob("*.jpg")) + list(d.glob("*.png"))):
        m = re.search(r"_(\d{6})_\d+", f.stem)
        if m:
            stamps[m.group(1)[:4]] += 1     # HHMM
    s = " ".join(f"{k}:{v}" for k, v in sorted(stamps.items()))
    print(f"  {lk} ({leaf_counts[lk]}張): {s if s else '(命名無時間)'}")

# 異常:非影像檔 / 壓縮檔 / MISMATCH
print("\n=== 其他檔案(非影像)===")
others = [p for p in ROOT.rglob("*") if p.is_file() and p.suffix.lower() not in (".jpg", ".png", ".bmp")]
for p in others[:25]:
    print(f"  {p.relative_to(ROOT)} ({p.stat().st_size/1e6:.1f} MB)")
print(f"  共 {len(others)} 個非影像檔")
