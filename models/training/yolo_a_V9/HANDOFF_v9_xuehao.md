# 交接文件 — 在另一台電腦訓練 V9 穴號（xuehao）

> 建立：2026-07-06。目標機：**GPU 訓練機（無任何本專案圖檔）**。
> 圖檔/權重稍後由**另一個 session 用 TCP/IP 傳過去**——本文件只負責「要傳什麼、傳到哪、怎麼跑」。
> 方法論＝A 軸「全量從頭重訓、不 warm-start」，模號 v9 已驗證成立，本次把同套路套到穴號。

---

## 0. 背景（為何要做穴號 v9）

- 模號 v9（全量重訓）已證實可避免 warm-start 的 catastrophic forgetting（M83 v6.7.3=55% → v9=99.9%，其餘不退）。
- 修好模號後，**穴號成為新瓶頸**：M17 線上 16 錯裡 15 個是穴號錯（M17/13→16 塌陷），因為 v9 只重訓了模號 head、**穴號 head 還是舊的**。
- 本次＝用同一套配方（全量、`yolov8s-cls` 起訓、固定 seed、零退步 gate）重訓**穴號 head**。

---

## 1. ★ 傳輸清單（目標機什麼都沒有，全部要傳）

**必須在目標機重建以下相對結構**（設 `<REPO>` = 專案根，例如 `D:\Content_lens_OCR`）：

| 來源（本機 `D:\Content_lens_OCR\`）| 目標機放置 | 大小 | 用途 |
|---|---|---|---|
| `data_v671\xuehao\`（train+val，穴號 01–18）| `<REPO>\data_v671\xuehao\` | ~40M（data_v671 全 87M）| 穴號 base 訓練資料（跨模池化）|
| `data3\M17\`（穴號 01–18 原圖 721）| `<REPO>\data3\M17\` | 49M | bulk M17 穴號料（併入訓練）|
| `data4\M17\`（穴號 01–18 原圖 361）| `<REPO>\data4\M17\` | 29M | **不進訓練**，未見探針（eval 用）|
| `yolov8s-cls.pt` | `<REPO>\yolov8s-cls.pt` | 13M | 從頭訓的 base backbone |
| `OCR\yolo_a_V6\v6_preprocess.py` | 同路徑 | 4K | imread_unicode/find_circle/white_pad_square |
| `OCR\yolo_a_V6.7\v67_dataset.py` | 同路徑 | 4K | R_INNER / annulus_polar |
| `OCR\yolo_a_V6.7.1\v671_aug_ops.py` | 同路徑 | 8K | apply_tier2/3、appearance_jitter |
| `OCR\yolo_a_V6.7.3\v673_dataset.py` | 同路徑 | 4K | Xuehao/MohaoMixedTierDataset |
| `OCR\yolo_a_V6.7.3\runs\xuehao\weights\best.pt` | 同路徑 | 10.3M | **穴號基準權重**（eval 零退步比較用）|
| `OCR\yolo_a_V9\_build_data_v9_xuehao.py` | 同路徑 | — | 建資料腳本 |
| `OCR\yolo_a_V9\_train_v9_xuehao.py` | 同路徑 | — | 訓練腳本 |
| `OCR\yolo_a_V9\_eval_v9_xuehao.py` | 同路徑 | — | 評估腳本 |

**選用（要在目標機跑穩定圖片區大規模驗證才需要）**：
- `D:\模號穴號-穩定圖片區\M17\`（穴號 06/07/08/10/11/17，各 ~344）— 較大，僅 M17 穴號驗證需要。

> 注意：目標機**不需要** `data_v671\mohao`、`data3` 以外的模號料——穴號訓練只吃 xuehao base + data3/data4 的 M17。

---

## 2. 目標機環境（需與本機一致以複現）

- Python 環境：conda `lens-gpu`（cu124）。本機路徑 `C:\Users\User\anaconda3\envs\lens-gpu\python.exe`；目標機用自己的對應 env。
- 套件版本（複現關鍵）：**Ultralytics 8.4.62 / torch 2.6.0+cu124 / Python 3.11**。
- GPU：本機為 RTX 3050 Laptop 4GB，batch=16 imgsz=640 可跑；目標機若 VRAM 更大可加 batch（但**驗證零退步時 seed 固定即可，batch 改變會改結果**，建議先照 batch=16 複現，要調再說）。
- Windows cv2 DataLoader 死鎖 → 腳本已內建 `cv2.setNumThreads(0)`。
- 中文路徑讀寫：腳本已用 `imread_unicode`(np.fromfile+imdecode) / `imencode+tofile`。

---

## 3. 執行步驟（目標機依序跑）

```bash
# 0) 確認傳輸清單就位（data_v671/xuehao、data3/M17、data4/M17、yolov8s-cls.pt、四支相依 .py、xuehao 基準 best.pt、三支 v9 xuehao 腳本）

# 1) 建資料：data_v671/xuehao 全穴號 + data3/M17 依穴號 ROI 化併入
python OCR/yolo_a_V9/_build_data_v9_xuehao.py
#   產出 data_v9/xuehao/{train,val}/01..18；印各穴號張數與 data3 ROI 成功率

# 2) 全量從頭重訓穴號（seed=0、deterministic、不 warm-start）
python OCR/yolo_a_V9/_train_v9_xuehao.py --device 0 --workers 4 --batch 16
#   產出 OCR/yolo_a_V9/runs/xuehao/weights/best.pt

# 3) 評估：v9 xuehao vs v6.7.3 xuehao 每穴號零退步 + data4/M17 未見探針
python OCR/yolo_a_V9/_eval_v9_xuehao.py --device 0
```

> 長時間步驟（訓練 ~20–40 分）建議背景執行 + 盯 log 完成標記（`Results saved to` / `epochs completed`）。

---

## 4. 判準與預期（A 軸穴號是否成立）

- **零退步 gate**：v9 穴號對 `data_v671/xuehao/val` **每一個穴號都 ≥ v6.7.3**。任一穴號掉即未過（比照模號做 best-of-N seed 擇優：跑 seed 0~4 挑零退步且新料最好那顆）。
- **學起 M17**：`data4/M17` 未見探針各穴號準確率要高（模號 v9 的對應探針是 361/361=100%，穴號目標同量級）。
- **重點觀察 M17/13、/16**：線上報告顯示 13/15/10 塌向 16。看重訓後這組混淆收斂多少：
  - 若**明顯收斂** → A 軸對穴號同樣成立，可換版。
  - 若**仍塌向 16** → 屬形狀/接縫混淆（非資料量問題），需再上 wrap-around 攤平 / 機率平均聚合 / 偵測（見 `.ai/records/2026-07-06/02_bug_notes.md`）。

---

## 5. 跑完要送回什麼

1. `OCR/yolo_a_V9/runs/xuehao/weights/best.pt`（v9 穴號權重）。
2. `_eval_v9_xuehao.py` 的輸出（每穴號零退步表 + data4 探針 + 判定）。
3. 訓練 log（含最終 top1_acc、實際 seed/batch）。

---

## 6. 相關檔案 / 紀錄

- 模號 v9 全套與結果：本資料夾 `_build_data_v9.py` / `_train_v9.py` / `_eval_v9.py` / `_val_stable_3molds.py`；權重 `runs/mohao/weights/best.pt`。
- 策略與 SOP：`OCR/_甲方自助重訓_SOP.md`。
- 當日紀錄：`.ai/records/2026-07/2026-07-06/{01_daily_log,02_bug_notes,03_reusable_flow}.md`。
- 全域記憶：`project_retrain_full_not_warmstart.md`。
