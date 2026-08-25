"""跑 8 張 demo：detector + non-AR CRNN 端到端結果視覺化。"""
import sys, re, random
from pathlib import Path
import cv2, numpy as np, torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode, crop_band, to_tensor
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES
from ultralytics import YOLO

HERE = Path(__file__).resolve().parent
DETECTOR = HERE / "runs" / "detector" / "weights" / "best.pt"
CRNN = HERE / "runs" / "nonar_stable_holdout_M54" / "best.pt"
OUT = HERE / "diag" / "demo_final"
OUT.mkdir(parents=True, exist_ok=True)
HALF_W = 100
XUEHAO_RE = re.compile(r"(?:exp_)?M\d+[-_](\d{2})_")

# 挑樣本：已知模號 3 張 + M54 未見 4 張 + stable 1 張
REPO = Path("D:/incoming/Content_lens_OCR")
STABLE = Path("D:/incoming/模號穴號-穩定圖片區/前處理區")
rng = random.Random(42)

known_samples = []
for mold in ["M17", "M60", "M83"]:
    strips = list((REPO / "data_v671_strips" / "mohao" / "val" / mold).glob("*.png"))
    if strips:
        known_samples.append((strips[0], mold))
m54_val_samples = [(p, "M54") for p in sorted((REPO / "data_v671_strips" / "mohao" / "val" / "M54").glob("*.png"))[:2]]
m54_stable_samples = [(p, "M54") for p in list((STABLE / "M54").rglob("*.png"))[:2]]
samples = known_samples + m54_val_samples + m54_stable_samples

# load models
device = torch.device("cuda:0")
det = YOLO(str(DETECTOR))
ckpt = torch.load(CRNN, map_location=device, weights_only=False)
model = NonAROCR(num_classes=NUM_CLASSES).to(device).eval()
model.load_state_dict(ckpt["model"])

rows = []
for src, true_mohao in samples:
    true_xuehao = XUEHAO_RE.search(src.stem)
    true_xuehao = true_xuehao.group(1) if true_xuehao else "??"
    img = imread_unicode(src)   # 640x640 strip
    r = det.predict(img, verbose=False, device=0, conf=0.25)[0]

    vis = img.copy()
    pred_mohao, pred_xuehao = "?", "?"
    m_crop, x_crop = None, None
    if r.boxes is not None and len(r.boxes) > 0:
        cls_arr = r.boxes.cls.cpu().numpy().astype(int)
        conf_arr = r.boxes.conf.cpu().numpy()
        xy_arr = r.boxes.xywh.cpu().numpy()
        m_idx = np.where(cls_arr == 0)[0]
        x_idx = np.where(cls_arr == 1)[0]
        band = crop_band(img)
        W = band.shape[1]
        crops_to_pred = []
        heads = []
        if len(m_idx) > 0:
            best = m_idx[np.argmax(conf_arr[m_idx])]
            cx = int(round(xy_arr[best][0]))
            m_crop = band[:, np.arange(cx - HALF_W, cx + HALF_W) % W]
            crops_to_pred.append(m_crop); heads.append("m")
            # 畫紅框在原圖 (band region [280:360])
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

    m_ok = "✓" if pred_mohao == true_mohao else "✗"
    x_ok = "✓" if pred_xuehao == true_xuehao else "✗"
    label_text = (f"true={true_mohao}-{true_xuehao}  "
                  f"pred={pred_mohao}-{pred_xuehao}  {m_ok}模 {x_ok}穴")
    caption = np.ones((40, vis.shape[1], 3), dtype=np.uint8) * 245
    color = (0, 128, 0) if (m_ok == "✓" and x_ok == "✓") else (0, 0, 200)
    cv2.putText(caption, label_text, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

    # crop 兩張顯示（放大 3× 讓字看清楚）
    crop_disp = np.ones((80 * 3, 200 * 3 * 2 + 30, 3), dtype=np.uint8) * 245
    if m_crop is not None:
        mv = cv2.resize(m_crop, (200 * 3, 80 * 3), interpolation=cv2.INTER_NEAREST)
        crop_disp[:80*3, :200*3] = mv
    if x_crop is not None:
        xv = cv2.resize(x_crop, (200 * 3, 80 * 3), interpolation=cv2.INTER_NEAREST)
        crop_disp[:80*3, 200*3+30:] = xv
    tag_bar = np.ones((25, crop_disp.shape[1], 3), dtype=np.uint8) * 220
    cv2.putText(tag_bar, "Mohao crop -> pred: " + pred_mohao, (10, 18),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
    cv2.putText(tag_bar, "Xuehao crop -> pred: " + pred_xuehao, (200*3+40, 18),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 0, 0), 1)
    # 統一寬度為 crop_disp.shape[1] (1230)
    target_w = crop_disp.shape[1]
    # vis (640x640) → resize 到 target_w 寬、保持比例
    new_h = int(vis.shape[0] * target_w / vis.shape[1])
    vis_r = cv2.resize(vis, (target_w, new_h))
    caption_r = cv2.copyMakeBorder(caption, 0, 0, 0, target_w - caption.shape[1],
                                    cv2.BORDER_CONSTANT, value=(245, 245, 245)) if caption.shape[1] < target_w else caption
    combined = np.vstack([caption_r, vis_r, tag_bar, crop_disp])
    rows.append(combined)

# 全部堆疊
sep = np.ones((20, rows[0].shape[1], 3), dtype=np.uint8) * 180
final_parts = []
for i, r in enumerate(rows):
    if i > 0:
        final_parts.append(sep)
    # pad width if 不齊
    if r.shape[1] < rows[0].shape[1]:
        pad_w = rows[0].shape[1] - r.shape[1]
        r = cv2.copyMakeBorder(r, 0, 0, 0, pad_w, cv2.BORDER_CONSTANT, value=(245, 245, 245))
    final_parts.append(r)
full = np.vstack(final_parts)

out_path = OUT / "demo_all.png"
ok, buf = cv2.imencode(".png", full, [cv2.IMWRITE_PNG_COMPRESSION, 3])
buf.tofile(str(out_path))
print(f"[done] wrote {out_path} shape={full.shape}")

# 也各存一張大圖
for i, r in enumerate(rows):
    p = OUT / f"demo_{i:02d}.png"
    ok, buf = cv2.imencode(".png", r, [cv2.IMWRITE_PNG_COMPRESSION, 3])
    buf.tofile(str(p))
print(f"[done] individual demos: {OUT}")
