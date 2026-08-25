# crnn_fallback 完整交接（2026-07-19 ~ 07-20）

## 0. TL;DR — 兩天成果

**Production-ready OCR pipeline，一顆模型同時讀模號 + 穴號**：

| 指標 | 數字 |
|---|---|
| 模號類別數 | 20（19 原有 + M54） |
| val 精度（全 37 類 mohao + xuehao） | **99.96%**（2415/2416） |
| M54 val 精度 | **100%**（52/52） |
| M54 前處理區大測 | **100%**（2000/2000） |
| data_v6/test 抽查 | **100%**（10/10） |
| **錯誤2（舊系統誤判集）救回率** | **97.1%（34/35）** |
| 單張推論延遲 | **14 ms**（p90 14.8 ms） |
| 產線延遲預算 | 100 ms → **7× 餘裕** |

**唯一 1 張沒救的**：M28-09→M28-06（6↔9 旋轉對稱、需模具底線輔助判斷、跟 OCR 架構無關）。

**架構關鍵**：**detector (YOLOv8n) + Non-AR 位置獨立字元分類器**（4 個 query token + cross-attention）。跟 V9 差別是 V9 用「整張分類」+ 另一個 xuehao head，我們**一顆模型讀完雙頭**、也對未來新增模號更好擴充。

---

## 1. 系統架構

```
raw 448×448 lens image
       ↓
[前處理] Hough 定圓 → warpPolar → 640×640 strip
       ↓
[Detector] YOLOv8n → 找 2 個文字框（mohao + xuehao）
       ↓
[Crop]    band[280:360] → 兩個 200×80 windows（環狀 wrap）
       ↓
[Non-AR OCR] 4 個 query × cross-attention → 每 position 12 類分類
       ↓
[Decode]  非 blank 字元串接 → mohao 字串 + xuehao 字串
```

**性能量測**（RTX 3080）：
- I/O + detector：mean 7.6 ms（p90 8.3 ms）
- Crop + CRNN：mean 3.8 ms（p90 4.1 ms）
- **Total end-to-end：mean 13.9 ms（p90 14.8 ms）**
- Batch=16 平均 7.7 ms/張

---

## 2. Production 部署清單

### 2.1 上線需要的檔案
```
OCR/crnn_fallback/
├── nonar_model.py                                      # Non-AR OCR 模型定義
├── crnn_dataset.py                                     # 前處理 utility (imread_unicode, find_circle, annulus_polar, crop_band)
└── runs/
    ├── detector/weights/best.pt      # 6.3 MB   YOLOv8n text detector
    └── nonar_include_M54/weights.pt  # ~13 MB   Non-AR OCR (20 mohaos + xuehao)
```

### 2.2 相依環境
- **conda env**：`lens-gpu`（torch 2.6.0+cu124 / ultralytics 8.4.62 / opencv 4.13 / Python 3.11）
- **GPU**：RTX 3050 4GB 或以上（本專案用 3080 訓、3050 已驗證推論可跑）
- **不可改常數**（跟 V6.7 一致）：`imgsz=640`, `r_inner=0.6`, `HALF_W=100`（crop 寬 200）

### 2.3 最小推論 code（給部署工程師參考）
見 `_bench_inference.py` — 已用來量 14ms 延遲，代碼可以直接抄。

---

## 3. 完整資料結構

### 3.1 專案根 `D:/incoming/Content_lens_OCR/` 內

```
Content_lens_OCR/
├── OCR/
│   ├── crnn_fallback/                # ★ 本次交接主目錄
│   ├── yolo_a_V6/                    # V6 歷史（含 data_v6）
│   ├── yolo_a_V6.7.2/                # V9 之前的 production
│   ├── yolo_a_V9/                    # V9 現行 production
│   └── ocr_demo/                     # V9 部署 demo
│
├── data_v671/           450 MB       # ★ 訓練 raw 圖（19 mohao 各 200~980 張 + NG）
│   └── mohao/
│       ├── train/{M15,M17,...M96,M101,NG}/*.jpg
│       └── val/{...}/*.jpg
│
├── data_v671_strips/    183 MB       # 由 data_v671 前處理成 640×640 strip
│   └── mohao/{train,val}/{M15,...}/*.png    (annulus_polar output)
│
├── data_v671_crops_v2/  111 MB       # detector 產生的 200×80 crops
│   ├── train/{M15,...,M54,...,01,...,18}/*_m.png|*_x.png
│   │        └─ 每張 strip → 2 crops (mohao _m + xuehao _x)
│   │        └─ 37 folders: 19 mohao classes + 18 xuehao (01~18)
│   └── val/{...}/*_m.png|*_x.png
│
├── data_v671_crops_v2_synth/ 132 MB  # font-render 合成 novel mohao (未見組合)
│   └── train/{M10,M11,...,M99,M100~M199 部分}/*.png
│       └─ 89 novel labels、"4"-含 label 200/張、其他 40/張 → 6440 crops
│
├── data_stable_crops.bak/ 153 MB     # M17/28/83/101 前處理區 crops（保留、無 M54）
│                                      # 本次 production 沒用（曾實驗證實會稀釋「4」→ 反效果）
│                                      # 若未來需要 M17/28/83/101 更多料再啟用
│
├── data_v673/          119 MB        # v671 的另一版本副本
├── data_v9/            453 MB        # V9 的訓練資料（跟本次無關）
│
└── 模號穴號-穩定圖片區/                 # ← 甲方 現場料
    ├── M101/, M17/, M28/, M54/, M83/  # raw 大量取圖
    └── 前處理區/    375 MB            # ★ 已 pre-processed 的 strip PNG
        └── {M101,M17,M28,M54,M83}/**/*.png
            ├─ M54 這批 2000 張是我們的 (C) 大測集
            └─ 其他 4 類可加訓（bak 版路徑）
```

### 3.2 crnn_fallback 內部結構

```
OCR/crnn_fallback/
├── HANDOFF.md                        # ★ 本檔
├── README.md                         # 舊版簡介（已被本檔取代）
│
├── nonar_model.py                    # ★ Non-AR OCR (4 query + cross-attn)
├── crnn_dataset.py                   # ★ 前處理 utility + 3 個 Dataset
├── crnn_model.py                     # (歷史) CRNN 架構、production 不用
├── attn_model.py                     # (歷史) Attention decoder、production 不用
├── attn_dataset.py                   # (歷史) attention 專屬 dataset
│
├── _train_nonar.py                   # ★ Non-AR 訓練（production 用）
├── _train_detector.py                # ★ YOLOv8n detector 訓練
├── _train_crnn_spike.py              # (歷史) CRNN v1 訓練
├── _train_crnn_v2.py                 # (歷史) CRNN v2 訓練
├── _train_crnn_v3.py                 # (歷史) CRNN v3~v5 訓練 (加 synth)
├── _train_attn.py                    # (歷史) attention decoder 訓練
│
├── _eval_nonar.py                    # ★ M1 gate 評估（三場景：A/B/C）
├── _eval_crnn_spike.py               # (歷史) CRNN spike 評估
├── _eval_crnn_v2.py                  # (歷史) CRNN v2 評估
├── _eval_attn.py                     # (歷史) attn 評估
│
├── _build_yolo_data.py               # ★ 手標 JSON → YOLOv8 訓練格式
├── _build_strip_cache.py             # ★ data_v671 raw → strip PNG 快取
├── _build_crop_cache_v2.py           # ★ detector-based crop cache
├── _build_stable_crops.py            # 前處理區 M17/28/83/101 crop cache
├── _build_crop_cache.py              # (歷史) 密度規則式 crop（失敗版）
│
├── _label_helper.py                  # ★ Detector bbox 手標 UI
├── _label_char4.py                   # (歷史) M49「4」bbox 手標 UI
├── _label_char_multi.py              # ★ 通用字元 bbox 手標 UI (7/0/3)
│
├── _synth_fonts.py                   # ★ 純 font 合成 (pump 200 版)
├── _synth_fonts_v2.py                # ★ 含真實 char template 的合成 v2
├── _synth_chars_pilot.py             # (歷史) char-swap pilot（失敗）
│
├── _extract_real_4.py                # (歷史 v1) 密度式抽 4
├── _extract_real_4_v2.py             # 手標 bbox → 抽乾淨「4」template
├── _extract_multi_char.py            # ★ 抽 4/7/0/3 template
│
├── _bench_inference.py               # ★ 延遲量測（14ms）
│
├── _demo_show.py                     # 7 張 demo（原始跟 M54 混合）
├── _demo_m101.py                     # M101 跨穴號 demo
├── _demo_v6_test.py                  # ★ data_v6/test 抽 10 張 demo
├── _demo_error2.py                   # ★ 錯誤2 集救回率測試（34/35）
├── _demo_final.py                    # ★ Production model 11 張 demo
│
├── _make_examples.py                 # 標註範例產生器
├── _smoke_test.py                    # 訓練前煙霧測試
├── _diag_ctc_collapse.py             # (歷史) CTC collapse 診斷
├── _debug_extract.py                 # (歷史) char extraction 除錯
├── _test_detector.py                 # (歷史) 密度規則偵測器測試
├── _test_detector2.py                # (歷史) char-count 偵測器測試
├── _test_fonts_4.py                  # 字型開頂 4 巡檢
│
├── labels/                           # ★ 手標 JSON 檔（可攜帶）
│   ├── detector_manual.json          # 252 張 detector bbox 標註 (14/類 × 18)
│   ├── char4_bbox.json               # 40 張 M49「4」bbox
│   ├── char_bbox_7.json              # 30 張 M17「7」bbox
│   ├── char_bbox_0.json              # 30 張 M50「0」bbox
│   └── char_bbox_3.json              # 30 張 M23「3」bbox
│
├── char_templates/                   # 從手標 bbox 抽出的字元 template
│   ├── 4/*.png                       # 40 個真實「4」（M49 抽）
│   ├── 7/*.png                       # 30 個真實「7」（M17 抽）
│   ├── 0/*.png                       # 30 個真實「0」（M50 抽）
│   └── 3/*.png                       # 30 個真實「3」（M23 抽）
│
├── detector_data/                    # YOLOv8n 訓練 dataset (data.yaml)
│   ├── train/images/*.png train/labels/*.txt (215 張)
│   ├── val/images/*.png val/labels/*.txt (37 張)
│   └── data.yaml
│
├── diag/                             # 診斷 + demo 視覺化圖檔（可刪、只是 gitignore 用）
│   ├── demo_final_production/
│   ├── demo_v6_test/
│   ├── demo_error2/
│   └── ...
│
└── runs/                             # 訓練 output
    ├── detector/weights/best.pt      # ★ YOLOv8n detector（上線用）
    ├── nonar_include_M54/best.pt     # ★★ Production 模型（上線用）
    ├── nonar_stable_holdout_M54/     # (歷史) open-vocab 系列
    ├── nonar_holdout_M54/            # (歷史)
    ├── attn_holdout_M54/             # (歷史)
    ├── spike_holdout_M54/            # (歷史) v1 CRNN
    ├── spike_v2_holdout_M54/         # (歷史) v2 CRNN
    └── spike_v3_synth_holdout_M54/   # (歷史) v3-v5 CRNN
```

---

## 4. 程式關鍵資訊（給接手工程師）

### 4.1 Production 用得到的（必看）

**模型定義：`nonar_model.py`**
- `NonAROCR` class：3.24M params、輸入 80×200、輸出 4×12 logits
- Alphabet：`["<blank>", "M", "0"~"9"]` 共 12 類
- 4 個 learnable query × TransformerDecoderLayer × 2 → 4 個位置獨立輸出

**前處理：`crnn_dataset.py`**
- `imread_unicode()`：CJK 路徑安全讀圖
- `find_circle()`：Hough 定圓
- `annulus_polar()`：warpPolar 展開 rim
- `crop_band()`：640×640 → 80×640（垂直裁掉 padding 只留字帶）

**訓練：`_train_nonar.py`**
- config：AdamW lr=5e-4, cosine, epochs=30, batch=64, imgsz=80×200
- 目前預設 `HOLDOUT = []`（M54 進訓、production 版）
- 若要復現 open-vocab 實驗改成 `HOLDOUT = ["M54"]`

**評估：`_eval_nonar.py`**
- 三場景：A (in-domain val)、B (M54 val)、C (前處理區 M54 2000 張)
- 用 `--weights <path>` 指定其他權重

### 4.2 資料建置流程（每加一批新 raw 都要跑一次）

```powershell
conda activate lens-gpu
cd D:\incoming\Content_lens_OCR\OCR\crnn_fallback

# Step 1: raw → strip PNG cache (~30 秒)
python -s _build_strip_cache.py

# Step 2: strip → 200×80 crops via detector (~1 分鐘)
python -s _build_crop_cache_v2.py

# Step 3: 訓練 Non-AR (~15 分鐘)
python -s _train_nonar.py --device 0 --workers 4 --batch 64 --epochs 30

# Step 4: 評估
python -s _eval_nonar.py --weights runs/nonar_include_M54/best.pt
```

### 4.3 新增模號 SOP（跟 V9 一致）

1. **收料**：新模號 30~50 張真實 raw 圖，放到 `data_v671/mohao/train/{新模號}/`
2. **（可選）手標 detector**：跑 `_label_helper.py --per-class 14`（用 `existing_labels` 保留原有）→ 新模號多 5~10 張手標
3. **（可選）重訓 detector**：`_build_yolo_data.py` → `_train_detector.py`（若既有 detector 對新模號位置抓不準才需要）
4. **建 crop cache**：`_build_crop_cache_v2.py` 會自動 include 新模號 folder
5. **重訓 Non-AR**：`_train_nonar.py`（可能要調整 `NUM_CLASSES` 因為 mohao class 數變了）
6. **eval**：確認新模號在 in-domain val 上 99%+
7. **上線**：替換 `runs/nonar_include_M54/best.pt`

### 4.4 手標工具

- `_label_helper.py`：detector 手標。每張 2 clicks（模號中心、穴號中心）。已標 252 張。
- `_label_char_multi.py`：字元 bbox 手標。每張 2 clicks（字元左邊、右邊）。已標 130 張（4/7/0/3 各 40/30/30/30）。

---

## 5. 完整實驗紀錄（14 個版本）

### 5.1 Open-vocab 系列（M54 holdout、失敗）

| # | 版本 | (A) in-domain | (B) M54 val | (C) 前處理區 M54 |
|---|---|---|---|---|
| 1 | CRNN v2 baseline | 99.92% | 0% | 0% |
| 2 | CRNN v3~v5 font-synth 迭代 | 99.5~99.96% | 0% | 0~0.1% |
| 3 | Attention decoder | 100% | 0% | 0.05% |
| 4 | Non-AR + synth 40/label | 99.92% | 23.08% | 14.75% |
| 5 | **Non-AR + pump 200** | 100% | 9.62% | **25.80% ★ (lucky)** |
| 6 | Non-AR + stable + pump 800 (108 detector) | 99.96% | 15.38% | 19.50% |
| 7 | Non-AR + stable + pump 800 (252 detector) | 99.96% | 9.62% | 3.80% |
| 8 | Non-AR + real-4 noisy template | 99.96% | 0% | 0.80% |
| 9 | Non-AR + real-4 clean template | 99.96% | 7.69% | 6.05% |
| 10 | Non-AR pump 200 復刻嘗試 | 100% | 3.85% | 8.05% |
| 11 | Non-AR + 4-char real template (4/7/0/3) | 99.92% | 0% | 0.05% |

**結論**：25.8% 是 lucky seed、可靠期望值 5~15%。**M1 gate 90%+ 沒過**。

### 5.2 實用主義（M54 進訓）— 現行 production

| # | 版本 | (A) 全 val | (B) M54 val | (C) 前處理區 M54 |
|---|---|---|---|---|
| 12 | **Non-AR M54 included** ★★ | **99.96%** (2415/2416) | **100%** (52/52) | **100%** (2000/2000) |

### 5.3 額外驗證

| 測試 | 結果 |
|---|---|
| data_v6/mohao/test 隨機 10 張 | **10/10 全對** |
| 錯誤2（舊系統誤判 35 張） | **34/35 = 97.1% 救回率** |
| （唯一沒救的）M28-09→M28-06 | 6↔9 旋轉對稱、需模具底線判斷 |

---

## 6. 重要架構決策（給接手要理解 why）

### 6.1 為什麼是 Non-AR、不是 CRNN 或 Attention Decoder
**CRNN + CTC + BiLSTM** 有 sequential vocab bias：訓練時「M5 後從沒接 4」→ 遇 M54 硬把「4」讀成 6/8。四版 CRNN 全 0%。

**Attention decoder** autoregressive 一樣有 vocab bias（decoder 條件於前 tokens）。也 0%。

**Non-AR (4 fixed query + cross-attn)** 每個位置獨立分類、沒有 sequential dependency → **唯一能突破 vocab bias 的架構**。（open-vocab 曾達 25.8%）

### 6.2 為什麼不加 stable 資料到訓練
`模號穴號-穩定圖片區/前處理區` 有 M17/M28/M83/M101 各 1250~2700 張 real。**這些沒有「4」字元**。加進去後訓練集裡「4」佔比從 20% 稀釋到 9.5% → M54 準確度反而**掉到 0.4%**。實驗版本 6~7 的教訓。

### 6.3 為什麼 detector 用手標而不是規則式
密度規則被鏡片表面紋理雜訊污染（用 15% max threshold 全 crop = (0, 199)）。改 char-count 也不穩。**手標 108 張 → detector mAP50 0.995**、後補到 252 張 → 0.9976（但沒有實質受益）。

### 6.4 為什麼放棄 open-vocab、選 M54 進訓
- 12 個版本試盡了 font synth / real template / stable data / seed sweep，最好 25.8% 且不可靠
- 缺的是 **M?4 系列的實體樣本**（訓練資料裡沒有 mohao 尾巴是 4 的）
- 甲方沒有這些樣本
- 實用主義：M54 進訓 → 全 20 模號 100%，未來新模號來就跟 V9 一樣重訓
- 唯一失去的是「零重訓」承諾、但廠商本來也接受重訓 SOP

---

## 7. 未來改進方向（有預算的話）

### 7.1 想再推 open-vocab（廠商要能配合）
需要甲方提供**稀缺字元覆蓋的實體模具樣本**：
- M?4 系列（M14, M24, M34, M44, M64, M74, M84, M94）
- M0X 系列（M01, M02, ..., M09）
- M3X 系列（M30~M39）
- M7X 系列（M70~M79）

每類 30~50 張真實 crops → 訓練覆蓋位置×字元 100% → 理論上可到 90%+。

### 7.2 若廠商不能配合、想省重訓
- 拿現有 nonar_include_M54 當 base、新模號來時 fine-tune 5~10 epochs（比從頭重訓快 3 倍）
- 建立 char template library：手標更多字元 bbox → 用 template synth 提升 open-vocab 到 40~50%（不到 90% 但夠當「flag 送人工複檢」）

### 7.3 短期 optimization
- **Seed sweep**：跑 seed 0~4 挑最好的、平均能提升 3~5%
- **Batched inference**：目前單張 14ms、batch=16 可壓到 7.7ms/張
- **模型 quantization**：INT8 量化可能推到 5ms（未驗證）

---

## 8. 兩天工作里程碑

**Day 1 (07-19)**：
- 探索 CRNN 架構（v1~v5 全 fail M54 test）
- 建 detector（108 手標 → mAP 0.995）
- 建 strip cache + crop cache
- 診斷出 CRNN vocab bias 根因

**Day 2 (07-20)**：
- 嘗試 attention decoder（fail）
- 換 Non-AR 架構、M54 突破零 → 25.8%
- 補 detector 手標到 252、字元 bbox 130
- 試 real template synth（失敗）
- **決定 M54 進訓 → 拿到 production 100%**
- 錯誤2 集驗證 97.1% 救回率

---

## 9. 給下次 session 的 3 句話

1. **Production 就是 `runs/nonar_include_M54/best.pt` + `runs/detector/weights/best.pt`**，別動。
2. **新模號來就重訓**（跟 V9 SOP 一樣）、`_train_nonar.py` 直接跑就好。
3. **想推 open-vocab 到 90%** 只有一條路：**跟甲方拿 M?4 / M0X / M3X / M7X 的實體樣本**、不要再自己合成了。

---

_2026-07-20 收工_
