"""量單張推論延遲：detector + non-AR CRNN 全 pipeline。"""
import sys, time
from pathlib import Path
import cv2, numpy as np, torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from crnn_dataset import imread_unicode, crop_band, to_tensor
from nonar_model import NonAROCR, decode_padded, NUM_CLASSES
from ultralytics import YOLO

HERE = Path(__file__).resolve().parent
DETECTOR = HERE / "runs" / "detector" / "weights" / "best.pt"
CRNN = HERE / "runs" / "nonar_stable_holdout_M54" / "best.pt"
HALF_W = 100

# load
device = torch.device("cuda:0")
det = YOLO(str(DETECTOR))
ckpt = torch.load(CRNN, map_location=device, weights_only=False)
model = NonAROCR(num_classes=NUM_CLASSES).to(device).eval()
model.load_state_dict(ckpt["model"])
print("[loaded] detector + non-AR CRNN")

# 拿 100 張 real strip 當 benchmark 樣本
strips_dir = Path("D:/incoming/模號穴號-穩定圖片區/前處理區/M54")
strips = list(strips_dir.rglob("*.png"))[:100]
print(f"[bench] {len(strips)} strips")

# warmup
img = imread_unicode(strips[0])
for _ in range(5):
    det.predict(img, verbose=False, device=0)
    band = crop_band(img)
    x = to_tensor(band[:, :200]).unsqueeze(0).to(device)
    with torch.no_grad():
        _ = model(x)
torch.cuda.synchronize()

# ---- (a) 單張 end-to-end ----
det_times, crnn_times, total_times = [], [], []
for p in strips:
    torch.cuda.synchronize()
    t0 = time.perf_counter()
    img = imread_unicode(p)
    t_read = time.perf_counter()
    # detector
    r = det.predict(img, verbose=False, device=0, conf=0.25)[0]
    torch.cuda.synchronize()
    t_det = time.perf_counter()
    # crop
    if r.boxes is None or len(r.boxes) == 0:
        continue
    cls_arr = r.boxes.cls.cpu().numpy().astype(int)
    conf_arr = r.boxes.conf.cpu().numpy()
    xy_arr = r.boxes.xywh.cpu().numpy()
    m_idx = np.where(cls_arr == 0)[0]
    x_idx = np.where(cls_arr == 1)[0]
    if len(m_idx) == 0 or len(x_idx) == 0:
        continue
    m_cx = int(round(xy_arr[m_idx[np.argmax(conf_arr[m_idx])]][0]))
    x_cx = int(round(xy_arr[x_idx[np.argmax(conf_arr[x_idx])]][0]))
    band = crop_band(img)
    W = band.shape[1]
    m_crop = band[:, np.arange(m_cx - HALF_W, m_cx + HALF_W) % W]
    x_crop = band[:, np.arange(x_cx - HALF_W, x_cx + HALF_W) % W]
    # CRNN 一次跑 mohao+xuehao 兩個 crop
    xin = torch.stack([to_tensor(m_crop), to_tensor(x_crop)], 0).to(device)
    with torch.no_grad():
        out = model(xin).argmax(-1).cpu().numpy()
    torch.cuda.synchronize()
    t_end = time.perf_counter()
    det_times.append((t_det - t_read) * 1000)
    crnn_times.append((t_end - t_det) * 1000)
    total_times.append((t_end - t0) * 1000)

print(f"\n--- SINGLE IMAGE END-TO-END (n={len(total_times)}) ---")
print(f"  I/O + detector: mean {np.mean(det_times):.1f}ms  p90 {np.percentile(det_times,90):.1f}ms")
print(f"  crop + CRNN:    mean {np.mean(crnn_times):.1f}ms  p90 {np.percentile(crnn_times,90):.1f}ms")
print(f"  TOTAL:          mean {np.mean(total_times):.1f}ms  p90 {np.percentile(total_times,90):.1f}ms")

# ---- (b) batched inference ----
print(f"\n--- BATCHED (batch=16) throughput ---")
imgs_batch = [imread_unicode(p) for p in strips[:16]]
torch.cuda.synchronize()
t0 = time.perf_counter()
results = det.predict(imgs_batch, verbose=False, device=0, conf=0.25)
torch.cuda.synchronize()
t_det = time.perf_counter()
# gather crops
all_crops = []
for r, img in zip(results, imgs_batch):
    if r.boxes is None or len(r.boxes) == 0: continue
    cls_arr = r.boxes.cls.cpu().numpy().astype(int)
    conf_arr = r.boxes.conf.cpu().numpy()
    xy_arr = r.boxes.xywh.cpu().numpy()
    for c_id in [0, 1]:
        idxs = np.where(cls_arr == c_id)[0]
        if len(idxs) == 0: continue
        best = idxs[np.argmax(conf_arr[idxs])]
        cx = int(round(xy_arr[best][0]))
        band = crop_band(img)
        W = band.shape[1]
        all_crops.append(band[:, np.arange(cx - HALF_W, cx + HALF_W) % W])
xin = torch.stack([to_tensor(c) for c in all_crops], 0).to(device)
with torch.no_grad():
    _ = model(xin).argmax(-1).cpu().numpy()
torch.cuda.synchronize()
t_end = time.perf_counter()
total = (t_end - t0) * 1000
per_img = total / 16
print(f"  16 imgs in {total:.1f}ms → per image {per_img:.1f}ms (det+crop+CRNN)")
