"""將 detector_manual.json (click 中心) → YOLOv8 detection 訓練資料集。

輸出：
  detector_data/
    train/images/*.png
    train/labels/*.txt   (每列: cls xc yc w h  normalized)
    val/images/*.png
    val/labels/*.txt
    data.yaml

Box 設定：固定 200 寬、100 高（帶點 vertical margin），y_center=0.5（strip 中央）。
class 0 = mohao, class 1 = xuehao。
"""
from __future__ import annotations
import json, random, shutil, sys
from pathlib import Path
import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode

HERE = Path(__file__).resolve().parent
LABELS_JSON = HERE / "labels" / "detector_manual.json"
DST = HERE / "detector_data"
STRIP_SIZE = 640
BOX_W = 200
BOX_H = 100  # 稍高於 80px band 給模型一點 tolerance

VAL_FRAC = 0.15
SEED = 0


def write_label(dst_txt: Path, mohao_x: int, xuehao_x: int) -> None:
    xc_m = mohao_x / STRIP_SIZE
    xc_x = xuehao_x / STRIP_SIZE
    yc = 0.5
    w = BOX_W / STRIP_SIZE
    h = BOX_H / STRIP_SIZE
    lines = [
        f"0 {xc_m:.6f} {yc:.6f} {w:.6f} {h:.6f}",  # mohao
        f"1 {xc_x:.6f} {yc:.6f} {w:.6f} {h:.6f}",  # xuehao
    ]
    dst_txt.write_text("\n".join(lines) + "\n", encoding="utf-8")


def copy_strip(src: Path, dst: Path) -> None:
    """走 imread + imencode 才能處理 CJK 路徑"""
    img = imread_unicode(src)
    dst.parent.mkdir(parents=True, exist_ok=True)
    ok, buf = cv2.imencode(".png", img, [cv2.IMWRITE_PNG_COMPRESSION, 3])
    if not ok:
        raise IOError(f"encode {src}")
    buf.tofile(str(dst))


def main() -> None:
    records = json.loads(LABELS_JSON.read_text(encoding="utf-8"))
    print(f"[data] loaded {len(records)} labels")

    rng = random.Random(SEED)
    rng.shuffle(records)
    n_val = int(len(records) * VAL_FRAC)
    val_set, train_set = records[:n_val], records[n_val:]
    print(f"[split] train={len(train_set)} val={len(val_set)}")

    if DST.exists():
        shutil.rmtree(DST)
    for split, items in [("train", train_set), ("val", val_set)]:
        img_dir = DST / split / "images"
        lbl_dir = DST / split / "labels"
        img_dir.mkdir(parents=True)
        lbl_dir.mkdir(parents=True)
        for r in items:
            src = Path(r["path"])
            # 避免重名：加上 mold prefix
            uniq = f"{r['mold']}_{src.stem}"
            copy_strip(src, img_dir / (uniq + ".png"))
            write_label(lbl_dir / (uniq + ".txt"), r["mohao_x"], r["xuehao_x"])

    yaml_txt = f"""path: {DST.as_posix()}
train: train/images
val: val/images
names:
  0: mohao
  1: xuehao
"""
    (DST / "data.yaml").write_text(yaml_txt, encoding="utf-8")
    print(f"[done] {DST / 'data.yaml'}")


if __name__ == "__main__":
    main()
