"""M101 (4-char mohao) 隨機穴號 demo。"""
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
OUT = HERE / "diag" / "demo_m101"
OUT.mkdir(parents=True, exist_ok=True)
HALF_W = 100
XUEHAO_RE = re.compile(r"(?:exp_)?M\d+[-_](\d{2})_")

STABLE = Path("D:/incoming/模號穴號-穩定圖片區/前處理區/M101")

# 抽 8 張不同穴號的 M101
strips = sorted(STABLE.rglob("*.png"))
rng = random.Random(7)
rng.shuffle(strips)

# 依穴號分桶再抽
by_xuehao = {}
for p in strips:
    m = XUEHAO_RE.search(p.stem)
    if m:
        by_xuehao.setdefault(m.group(1), []).append(p)
xuehao_keys = sorted(by_xuehao.keys())
print(f"[M101] found xuehao classes: {xuehao_keys}")

# 挑 8 個不同的穴號 (若有)
picked = []
for k in xuehao_keys[:8]:
    picked.append((by_xuehao[k][0], "M101"))

device = torch.device("cuda:0")
det = YOLO(str(DETECTOR))
ckpt = torch.load(CRNN, map_location=device, weights_only=False)
model = NonAROCR(num_classes=NUM_CLASSES).to(device).eval()
model.load_state_dict(ckpt["model"])

rows = []
correct_m = correct_x = correct_both = 0
for src, true_mohao in picked:
    m = XUEHAO_RE.search(src.stem)
    true_xuehao = m.group(1) if m else "??"
    img = imread_unicode(src)
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
        crops_to_pred = []; heads = []
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

    m_ok = (pred_mohao == true_mohao)
    x_ok = (pred_xuehao == true_xuehao)
    correct_m += int(m_ok); correct_x += int(x_ok); correct_both += int(m_ok and x_ok)
    tag = ("OK   " if (m_ok and x_ok) else "wrong")
    print(f"  {tag}  true={true_mohao}-{true_xuehao}  pred={pred_mohao}-{pred_xuehao}")

    label_text = f"true={true_mohao}-{true_xuehao}  pred={pred_mohao}-{pred_xuehao}"
    caption = np.ones((40, 1230, 3), dtype=np.uint8) * 245
    color = (0, 128, 0) if (m_ok and x_ok) else (0, 0, 200)
    cv2.putText(caption, label_text, (10, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

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
    target_w = crop_disp.shape[1]
    new_h = int(vis.shape[0] * target_w / vis.shape[1])
    vis_r = cv2.resize(vis, (target_w, new_h))
    combined = np.vstack([caption, vis_r, tag_bar, crop_disp])
    rows.append(combined)

print(f"\n[summary] mohao {correct_m}/{len(picked)}  xuehao {correct_x}/{len(picked)}  both {correct_both}/{len(picked)}")

for i, r in enumerate(rows):
    out = OUT / f"m101_{i:02d}.png"
    ok, buf = cv2.imencode(".png", r, [cv2.IMWRITE_PNG_COMPRESSION, 3])
    buf.tofile(str(out))
print(f"[done] wrote {len(rows)} demos → {OUT}")
