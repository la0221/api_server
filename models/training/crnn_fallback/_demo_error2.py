"""跑全部 錯誤2 案例、看新模型能修多少舊系統誤判。"""
import sys, re
from pathlib import Path
from collections import Counter
import cv2, numpy as np, torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import (imread_unicode, find_circle, white_pad_square,
                          annulus_polar, crop_band, to_tensor)
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES
from ultralytics import YOLO

HERE = Path(__file__).resolve().parent
DETECTOR = HERE / "runs" / "detector" / "weights" / "best.pt"
CRNN = HERE / "runs" / "nonar_include_M54" / "best.pt"
OUT = HERE / "diag" / "demo_error2"
OUT.mkdir(parents=True, exist_ok=True)
HALF_W = 100

# 檔名格式：{TRUE}_rotXXX_got_{OLD_PRED}.jpg  例如 M101-03_rot050_got_M101-06.jpg
LABEL_RE = re.compile(r"(M\d+)-(\d{2})_rot\d+_got_(M\d+)-(\d{2})")

ERROR_DIR = Path("D:/incoming/Content_lens_OCR/錯誤2/錯誤")
all_files = list(ERROR_DIR.rglob("*.jpg"))
print(f"[error2] {len(all_files)} error cases")

device = torch.device("cuda:0")
det = YOLO(str(DETECTOR))
ckpt = torch.load(CRNN, map_location=device, weights_only=False)
model = NonAROCR(num_classes=NUM_CLASSES).to(device).eval()
model.load_state_dict(ckpt["model"])

def infer(raw):
    circ = find_circle(raw)
    if circ is None:
        strip = white_pad_square(raw, 640)
    else:
        cx, cy, r = circ
        x0, y0 = max(0, cx - r), max(0, cy - r)
        x1, y1 = min(raw.shape[1], cx + r), min(raw.shape[0], cy + r)
        roi = white_pad_square(raw[y0:y1, x0:x1], target=2 * r)
        strip = annulus_polar(roi, do_rotate=False, size=640)
    r = det.predict(strip, verbose=False, device=0, conf=0.25)[0]
    pred_m, pred_x = "?", "?"
    m_crop, x_crop, m_cx, x_cx = None, None, None, None
    if r.boxes is not None and len(r.boxes) > 0:
        cls_arr = r.boxes.cls.cpu().numpy().astype(int)
        conf_arr = r.boxes.conf.cpu().numpy()
        xy_arr = r.boxes.xywh.cpu().numpy()
        m_idx = np.where(cls_arr == 0)[0]
        x_idx = np.where(cls_arr == 1)[0]
        band = crop_band(strip)
        W = band.shape[1]
        crops_to_pred, heads = [], []
        if len(m_idx) > 0:
            best = m_idx[np.argmax(conf_arr[m_idx])]
            m_cx = int(round(xy_arr[best][0]))
            m_crop = band[:, np.arange(m_cx - HALF_W, m_cx + HALF_W) % W]
            crops_to_pred.append(m_crop); heads.append("m")
        if len(x_idx) > 0:
            best = x_idx[np.argmax(conf_arr[x_idx])]
            x_cx = int(round(xy_arr[best][0]))
            x_crop = band[:, np.arange(x_cx - HALF_W, x_cx + HALF_W) % W]
            crops_to_pred.append(x_crop); heads.append("x")
        if crops_to_pred:
            xin = torch.stack([to_tensor(c) for c in crops_to_pred], 0).to(device)
            with torch.no_grad():
                preds = model(xin).argmax(-1).cpu().numpy()
            for h, p in zip(heads, preds):
                s = decode_padded(p.tolist())
                if h == "m": pred_m = s
                else: pred_x = s
    return strip, pred_m, pred_x, m_crop, x_crop, m_cx, x_cx

n_total = 0; n_new_correct = 0; n_new_wrong = 0
still_wrong = []
rows = []
for src in all_files:
    m = LABEL_RE.search(src.stem)
    if not m: continue
    true_m, true_x, old_m, old_x = m.group(1), m.group(2), m.group(3), m.group(4)
    raw = imread_unicode(src)
    strip, pred_m, pred_x, m_crop, x_crop, m_cx, x_cx = infer(raw)

    n_total += 1
    m_ok = pred_m == true_m
    x_ok = pred_x == true_x
    if m_ok and x_ok:
        n_new_correct += 1
    else:
        n_new_wrong += 1
        still_wrong.append((src.name, f"{true_m}-{true_x}", f"{pred_m}-{pred_x}", f"{old_m}-{old_x}"))

    # 儲存視覺化
    label_text = f"true={true_m}-{true_x}  old_sys={old_m}-{old_x}  NEW={pred_m}-{pred_x}"
    caption = np.ones((40, 1230, 3), dtype=np.uint8) * 245
    color = (0, 128, 0) if (m_ok and x_ok) else (0, 0, 200)
    cv2.putText(caption, label_text, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
    vis = strip.copy()
    if m_cx is not None:
        cv2.rectangle(vis, (m_cx - HALF_W, 280), (m_cx + HALF_W, 360), (0, 0, 255), 3)
    if x_cx is not None:
        cv2.rectangle(vis, (x_cx - HALF_W, 280), (x_cx + HALF_W, 360), (255, 0, 0), 3)
    raw_disp = cv2.resize(raw, (400, 400))
    strip_disp = cv2.resize(vis, (820, 400))
    top = np.hstack([raw_disp, np.ones((400, 10, 3), dtype=np.uint8) * 200, strip_disp])
    top = cv2.copyMakeBorder(top, 0, 0, 0, max(0, 1230 - top.shape[1]), cv2.BORDER_CONSTANT, value=(245,245,245))
    crop_disp = np.ones((80 * 3, 200 * 3 * 2 + 30, 3), dtype=np.uint8) * 245
    if m_crop is not None:
        crop_disp[:80*3, :200*3] = cv2.resize(m_crop, (200*3, 80*3), interpolation=cv2.INTER_NEAREST)
    if x_crop is not None:
        crop_disp[:80*3, 200*3+30:] = cv2.resize(x_crop, (200*3, 80*3), interpolation=cv2.INTER_NEAREST)
    rows.append(np.vstack([caption, top, crop_disp]))

print(f"\n=== 錯誤2 (舊系統誤判集) N={n_total} ===")
print(f"  新模型讀對: {n_new_correct}/{n_total} = {n_new_correct/n_total*100:.1f}%")
print(f"  新模型讀錯: {n_new_wrong}/{n_total} = {n_new_wrong/n_total*100:.1f}%")
if still_wrong:
    print(f"\n仍讀錯（新模型救不了的舊誤判）：")
    for fn, t, new_p, old_p in still_wrong:
        print(f"  true={t}  new={new_p}  old={old_p}  ({fn})")

for i, r in enumerate(rows):
    out = OUT / f"err_{i:02d}.png"
    ok, buf = cv2.imencode(".png", r, [cv2.IMWRITE_PNG_COMPRESSION, 3])
    buf.tofile(str(out))
print(f"[done] wrote {len(rows)} → {OUT}")
