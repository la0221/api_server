"""從 yolo_a_V6/data_v6/mohao/test 隨機抽 10 張、走完整 pipeline (raw → strip → detector → OCR)。"""
import sys, re, random, glob
from pathlib import Path
import cv2, numpy as np, torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import (imread_unicode, find_circle, white_pad_square,
                          annulus_polar, crop_band, to_tensor)
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES
from ultralytics import YOLO

HERE = Path(__file__).resolve().parent
DETECTOR = HERE / "runs" / "detector" / "weights" / "best.pt"
CRNN = HERE / "runs" / "nonar_include_M54" / "best.pt"
OUT = HERE / "diag" / "demo_v6_test"
OUT.mkdir(parents=True, exist_ok=True)
HALF_W = 100
XUEHAO_RE = re.compile(r"M\d+_(\d{2})_")

# 抽 10 張跨模號
V6_TEST = Path("D:/incoming/Content_lens_OCR/OCR/yolo_a_V6/data_v6/mohao/test")
molds = ["M15", "M17", "M28", "M49", "M54", "M58", "M60", "M83", "M96", "M101"]
rng = random.Random(42)
samples = []
for mold in molds:
    files = list((V6_TEST / mold).glob("*.jpg"))
    if files:
        samples.append((rng.choice(files), mold))

device = torch.device("cuda:0")
det = YOLO(str(DETECTOR))
ckpt = torch.load(CRNN, map_location=device, weights_only=False)
model = NonAROCR(num_classes=NUM_CLASSES).to(device).eval()
model.load_state_dict(ckpt["model"])

n_correct_both = 0; n_correct_head = 0
rows = []
for src, true_mohao in samples:
    xm = XUEHAO_RE.search(src.stem)
    true_xuehao = xm.group(1) if xm else "??"

    raw = imread_unicode(src)   # 448x448
    # 前處理：Hough + polar
    circ = find_circle(raw)
    if circ is None:
        strip = white_pad_square(raw, 640)
    else:
        cx, cy, r = circ
        x0, y0 = max(0, cx - r), max(0, cy - r)
        x1, y1 = min(raw.shape[1], cx + r), min(raw.shape[0], cy + r)
        roi = white_pad_square(raw[y0:y1, x0:x1], target=2 * r)
        strip = annulus_polar(roi, do_rotate=False, size=640)

    # detector
    r = det.predict(strip, verbose=False, device=0, conf=0.25)[0]
    vis = strip.copy()
    pred_mohao, pred_xuehao = "?", "?"
    m_crop, x_crop = None, None
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
            cx = int(round(xy_arr[best][0]))
            m_crop = band[:, np.arange(cx - HALF_W, cx + HALF_W) % W]
            crops_to_pred.append(m_crop); heads.append("m")
            cv2.rectangle(vis, (cx - HALF_W, 280), (cx + HALF_W, 360), (0, 0, 255), 3)
            cv2.putText(vis, "M", (cx - HALF_W + 5, 275), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)
        if len(x_idx) > 0:
            best = x_idx[np.argmax(conf_arr[x_idx])]
            cx = int(round(xy_arr[best][0]))
            x_crop = band[:, np.arange(cx - HALF_W, cx + HALF_W) % W]
            crops_to_pred.append(x_crop); heads.append("x")
            cv2.rectangle(vis, (cx - HALF_W, 280), (cx + HALF_W, 360), (255, 0, 0), 3)
            cv2.putText(vis, "X", (cx - HALF_W + 5, 275), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 0, 0), 2)
        if crops_to_pred:
            xin = torch.stack([to_tensor(c) for c in crops_to_pred], 0).to(device)
            with torch.no_grad():
                preds = model(xin).argmax(-1).cpu().numpy()
            for h, p in zip(heads, preds):
                s = decode_padded(p.tolist())
                if h == "m": pred_mohao = s
                else: pred_xuehao = s

    m_ok = pred_mohao == true_mohao
    x_ok = pred_xuehao == true_xuehao
    n_correct_head += int(m_ok) + int(x_ok)
    n_correct_both += int(m_ok and x_ok)
    tag = "OK   " if (m_ok and x_ok) else "wrong"
    print(f"  {tag}  true={true_mohao}-{true_xuehao}  pred={pred_mohao}-{pred_xuehao}  ({src.name})")

    label_text = f"true={true_mohao}-{true_xuehao}  pred={pred_mohao}-{pred_xuehao}"
    caption = np.ones((40, 1230, 3), dtype=np.uint8) * 245
    color = (0, 128, 0) if (m_ok and x_ok) else (0, 0, 200)
    cv2.putText(caption, label_text, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
    # 併 raw + strip 一起顯示
    raw_disp = cv2.resize(raw, (400, 400))
    strip_disp = cv2.resize(vis, (820, 400))
    top = np.hstack([raw_disp, np.ones((400, 10, 3), dtype=np.uint8) * 200, strip_disp])
    top = cv2.copyMakeBorder(top, 0, 0, 0, 1230 - top.shape[1], cv2.BORDER_CONSTANT, value=(245,245,245))
    crop_disp = np.ones((80 * 3, 200 * 3 * 2 + 30, 3), dtype=np.uint8) * 245
    if m_crop is not None:
        mv = cv2.resize(m_crop, (200 * 3, 80 * 3), interpolation=cv2.INTER_NEAREST)
        crop_disp[:80*3, :200*3] = mv
    if x_crop is not None:
        xv = cv2.resize(x_crop, (200 * 3, 80 * 3), interpolation=cv2.INTER_NEAREST)
        crop_disp[:80*3, 200*3+30:] = xv
    tag_bar = np.ones((25, crop_disp.shape[1], 3), dtype=np.uint8) * 220
    cv2.putText(tag_bar, f"Mohao pred: {pred_mohao}", (10, 18),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
    cv2.putText(tag_bar, f"Xuehao pred: {pred_xuehao}", (200*3+40, 18),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 0, 0), 1)
    combined = np.vstack([caption, top, tag_bar, crop_disp])
    rows.append(combined)

print(f"\n[summary] {n_correct_both}/{len(samples)} both correct, "
      f"{n_correct_head}/{len(samples)*2} heads correct")

for i, r in enumerate(rows):
    out = OUT / f"v6_{i:02d}.png"
    ok, buf = cv2.imencode(".png", r, [cv2.IMWRITE_PNG_COMPRESSION, 3])
    buf.tofile(str(out))
print(f"[done] wrote {len(rows)} → {OUT}")
