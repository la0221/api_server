"""簡易標註 UI —
   每張 strip 兩下點擊：先點模號中心、再點穴號中心（LEFT click）。

按鍵：
  Enter    存這張、下一張
  Esc      跳過這張、下一張
  u        Undo 上一次點擊
  b        回上一張重標
  q        存目前進度並離開

自動抽樣：每個非 M54 模號抽 6 張 → ~108 張。
標註存 JSON：{"path", "mohao_x", "xuehao_x"}。
"""
from __future__ import annotations

import argparse, json, random, sys
from pathlib import Path
import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode, crop_band

REPO = Path(__file__).resolve().parents[2]
STRIPS = REPO / "data_v671_strips" / "mohao"
LABELS = Path(__file__).resolve().parent / "labels" / "detector_manual.json"
LABELS.parent.mkdir(parents=True, exist_ok=True)
ZOOM = 2  # 顯示放大倍率
BAND_H = 80
BAND_W = 640
DISP_H = BAND_H * ZOOM
DISP_W = BAND_W * ZOOM


def sample_strips(per_class: int = 6, exclude: set[str] = {"M54", "NG"}, seed: int = 0,
                  existing_labels: set[str] | None = None) -> list[Path]:
    """先保留 existing_labels 裡已標的、每類補到 per_class。"""
    rng = random.Random(seed)
    existing_set = existing_labels or set()
    out: list[Path] = []
    for cls in sorted((STRIPS / "train").iterdir()):
        if cls.name in exclude or not cls.is_dir():
            continue
        pool = sorted(cls.glob("*.png"))
        already = [p for p in pool if str(p) in existing_set]
        need = per_class - len(already)
        if need > 0:
            unlabeled = [p for p in pool if str(p) not in existing_set]
            extra = rng.sample(unlabeled, min(need, len(unlabeled)))
            out.extend(already + extra)
        else:
            out.extend(already[:per_class])
    rng.shuffle(out)
    return out


def load_progress() -> dict[str, dict]:
    if LABELS.exists():
        return {r["path"]: r for r in json.loads(LABELS.read_text(encoding="utf-8"))}
    return {}


def save_progress(records: dict[str, dict]) -> None:
    LABELS.write_text(json.dumps(list(records.values()), indent=2, ensure_ascii=False),
                      encoding="utf-8")


def render(band: np.ndarray, clicks: list[tuple[int, int]]) -> np.ndarray:
    disp = cv2.resize(band, (DISP_W, DISP_H), interpolation=cv2.INTER_NEAREST)
    tags = ["M", "X"]
    colors = [(0, 0, 255), (255, 0, 0)]
    for i, (x, y) in enumerate(clicks):
        dx, dy = x * ZOOM, y * ZOOM
        cv2.line(disp, (dx, 0), (dx, DISP_H - 1), colors[i], 1)
        cv2.rectangle(disp, (max(0, dx - 96 * ZOOM), 0),
                      (min(DISP_W - 1, dx + 96 * ZOOM), DISP_H - 1), colors[i], 2)
        cv2.putText(disp, tags[i], (max(2, dx - 20), 20),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, colors[i], 2)
    banner = np.ones((30, DISP_W, 3), dtype=np.uint8) * 245
    txt = f"click {len(clicks)}/2 (M then X)  Enter=save  Esc=skip  u=undo  b=back  q=quit"
    cv2.putText(banner, txt, (10, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 0), 1)
    return np.vstack([banner, disp])


def label_loop(pool: list[Path]) -> None:
    records = load_progress()
    idx = 0
    while idx < len(pool):
        p = pool[idx]
        key = str(p)
        # 已標過 → 跳
        if key in records and "mohao_x" in records[key]:
            idx += 1
            continue

        strip = imread_unicode(p)
        band = crop_band(strip)
        clicks: list[tuple[int, int]] = []

        def cb(event, x, y, flags, param):
            if event == cv2.EVENT_LBUTTONDOWN and len(clicks) < 2:
                bx = min(BAND_W - 1, max(0, x // ZOOM))
                by = min(BAND_H - 1, max(0, (y - 30) // ZOOM)) if y >= 30 else 40
                clicks.append((bx, by))
                cv2.imshow("label", render(band, clicks))

        cv2.namedWindow("label", cv2.WINDOW_AUTOSIZE)
        cv2.setMouseCallback("label", cb)
        cv2.setWindowTitle("label", f"[{idx+1}/{len(pool)}] {p.parent.name} / {p.name}")
        cv2.imshow("label", render(band, clicks))

        act = None
        while act is None:
            k = cv2.waitKey(20) & 0xFF
            if k == 13:  # Enter
                if len(clicks) == 2:
                    records[key] = {"path": key, "mold": p.parent.name,
                                     "mohao_x": clicks[0][0], "xuehao_x": clicks[1][0]}
                    save_progress(records)
                    act = "next"
                else:
                    print(f"  need 2 clicks (got {len(clicks)})")
            elif k == 27:  # Esc
                act = "next"
            elif k == ord("u"):
                if clicks:
                    clicks.pop()
                    cv2.imshow("label", render(band, clicks))
            elif k == ord("b"):
                idx = max(-1, idx - 2)
                act = "next"
            elif k == ord("q"):
                cv2.destroyAllWindows()
                print(f"[quit] saved {len(records)} labels → {LABELS}")
                return
        idx += 1
    cv2.destroyAllWindows()
    print(f"[done] all {len(pool)} strips labeled → {LABELS}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--per-class", type=int, default=14)  # 14×18 = 252
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    # 讀取既有標籤，保留不重標
    existing = load_progress()
    existing_paths = set(existing.keys())
    pool = sample_strips(per_class=args.per_class, seed=args.seed,
                         existing_labels=existing_paths)
    new_to_label = [p for p in pool if str(p) not in existing_paths]
    print(f"[pool] target {len(pool)} strips ({args.per_class}/類)  "
          f"已標 {len(existing_paths)}  待標 {len(new_to_label)}")
    print(f"[UI] band displayed at {ZOOM}× ({DISP_H}×{DISP_W}). "
          f"click centers, Enter to save. {LABELS}")
    label_loop(pool)


if __name__ == "__main__":
    main()
