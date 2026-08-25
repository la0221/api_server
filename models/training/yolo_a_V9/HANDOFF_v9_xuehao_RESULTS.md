# 交接回程 — V9 穴號訓練結果（回傳給 dev 端）

> 建立：2026-07-07。從：**GPU 訓練機**。給：**dev 端 / 另一個 session**。
> 本文件是「上一份 HANDOFF_v9_xuehao.md（去程）」的**回程對照**，回報實際跑出的結果 + 要傳什麼檔回去。

---

## 0. TL;DR

- ✅ **執行完成**：依照 HANDOFF §3 三步驟跑完（build → train seed=0 → eval）。
- ✅ **M17 塌陷完全修好**：`data4/M17` 未見探針 **361/361 = 100.0%**（每穴號 20 張全對，含之前線上 13/15/10 → 16 的問題群）。
- ⚠ **零退步 gate 未過 seed=0**：穴 09 掉 1 張（100→98.4%）、穴 18 掉 1 張（100→98.6%）。
- ⏸ **未執行 seed 1~4 sweep**（時間成本 ~72 min）— 決策留給 dev 端：接受 seed=0 或後續補跑 sweep。

---

## 1. ★ 要回傳的檔案（從 GPU 機 → dev 端）

**僅回傳這次新增/更新的成果**，其他既有檔（v6.7.x、data_v65、data3、data4、yolov8s-cls.pt 等）dev 端已有。

| 來源（GPU 機 `D:\incoming\Content_lens_OCR\`）| 大小 | 用途 |
|---|---|---|
| `OCR\yolo_a_V9\runs\xuehao\weights\best.pt` | ~10.3 MB | **★ V9 穴號權重（seed=0）** |
| `OCR\yolo_a_V9\runs\xuehao\weights\last.pt` | ~10.3 MB | 最後 epoch 權重（可選）|
| `OCR\yolo_a_V9\runs\xuehao\results.csv` | 訓練曲線 | epoch × val_acc 記錄 |
| `OCR\yolo_a_V9\runs\xuehao\args.yaml` | 訓練參數 | 完整 override 記錄 |
| `OCR\yolo_a_V9\_train_xuehao_seed0.log` | 訓練 log | 20 ep 每輪 stdout（含 val 表現）|
| `OCR\yolo_a_V9\HANDOFF_v9_xuehao_RESULTS.md` | 本檔 | 決策脈絡 |

**選用**（若 dev 端想重新產出 data_v9 而非只驗證權重）：
- `data_v9\xuehao\{train,val}\01..18` — ~350 MB（重跑 `_build_data_v9_xuehao.py` 可再現，不建議傳）

---

## 2. 訓練配方（實跑數字，複現用）

| 項目 | 值 |
|---|---|
| 版本 | V9 穴號 seed=0 |
| 前處理 | 環狀 warpPolar（`R_INNER=0.6`） |
| Base backbone | **`yolov8s-cls.pt`（ImageNet 冷起，非 warm-start）** |
| 資料集 | `data_v9/xuehao`（v671 xuehao base 全 18 穴 + data3/M17 依穴號 ROI 化併入 train）|
| Train / Val 數 | **6482 train / 1232 val** |
| data3/M17 加入量 | 每穴號 26–39 張（原始 721 張，ROI 化成功 637 = 88.4%）|
| Dataset class | `XuehaoMixedTierDataset`（tier1=只旋轉、不外觀抖動）|
| Optimizer | AdamW，lr0=5e-4，lrf=0.1，cos_lr=True，warmup 0 |
| Epochs / Batch / imgsz | 20 / 16 / 640 |
| Seed / deterministic | **0 / True** |
| Patience | 8 |
| 執行時間 | ~18 min（RTX 3080 10GB）|
| 訓練曲線 best | ep **18**（top1=99.3% / top5=99.8%）|

---

## 3. 完整 val 每穴號結果

| 穴號 | v6.7.3 xuehao（baseline） | **V9 xuehao seed=0** | 差 |
|---|---|---|---|
| 01 | 65/65 = 100.0% | 65/65 = 100.0% | = |
| 02 | 64/64 = 100.0% | 64/64 = 100.0% | = |
| 03 | 88/89 = 98.9% | 88/89 = 98.9% | = |
| 04 | 73/73 = 100.0% | 73/73 = 100.0% | = |
| 05 | 67/67 = 100.0% | 67/67 = 100.0% | = |
| 06 | 61/66 = 92.4% | 61/66 = 92.4% | = |
| 07 | 70/70 = 100.0% | 70/70 = 100.0% | = |
| 08 | 63/63 = 100.0% | 63/63 = 100.0% | = |
| **09** | **62/62 = 100.0%** | **61/62 = 98.4%** | **↓ 1.6%** |
| 10 | 61/61 = 100.0% | 61/61 = 100.0% | = |
| 11 | 71/72 = 98.6% | 71/72 = 98.6% | = |
| 12 | 70/70 = 100.0% | 70/70 = 100.0% | = |
| 13 | 70/70 = 100.0% | 70/70 = 100.0% | = |
| 14 | 65/65 = 100.0% | 65/65 = 100.0% | = |
| 15 | 70/70 = 100.0% | 70/70 = 100.0% | = |
| 16 | 65/65 = 100.0% | 65/65 = 100.0% | = |
| 17 | 70/70 = 100.0% | 70/70 = 100.0% | = |
| **18** | **70/70 = 100.0%** | **69/70 = 98.6%** | **↓ 1.4%** |

**總計**：v6.7.3 raw val 準確度 ≈ v9 raw val 準確度，僅 09/18 各微退 1 張。

---

## 4. Data4/M17 未見探針（★ 核心目標達成）

**每穴號 20 張，共 361 張（穴 09 有 21 張）；未進訓練，測 M17 各穴號泛化能力**：

```
所有 18 個穴號 (01–18)：20/20 或 21/21 = 100.0%
合計 361/361 = 100.0%
```

**意義**：
- 這正是 A 軸「全量從頭重訓」要證明的事：M17 各穴號都學會了。
- v6.7.3 xuehao 於線上發生的 M17/13、/15、/10 塌向 16 的錯誤 → **在 v9 完全消失**。
- 資料量問題已用 data3/M17 併入解決，不是形狀/接縫混淆。

---

## 5. A 軸判定 & 決策

依 HANDOFF §4 判準：
> 「零退步 gate：v9 穴號對 `data_v671/xuehao/val` **每一個穴號都 ≥ v6.7.3**。任一穴號掉即未過。」

- **嚴格判定**：seed=0 **未過零退步 gate**（穴 09、18 各退步 1 張）。
- **實務判定**：核心目標「M17 泛化」達成（100%），退步只 -2/1232 = -0.16%，且退步的 09/18 是 v6.7.3 剛好滿分的邊界。

### 未執行的動作

- **未跑 seed 1~4 sweep**（每個 seed ~18 min，4 個 = ~72 min）— 決策留 dev 端：
  - **Path A**：dev 端接手跑 seed 1~4 sweep，挑零退步且新料最好的那顆。
  - **Path B**：直接接受 seed=0（因主要目標已達成）並轉入部署。

---

## 6. 若 dev 端要跑 seed sweep（Path A）

在 dev 端執行：

```powershell
# 備份 seed 0 權重
$py = "C:\Users\<user>\anaconda3\envs\lens-gpu\python.exe"
cd OCR\yolo_a_V9
Copy-Item runs\xuehao\weights\best.pt runs\xuehao\weights\best_seed0.pt

# 跑 seed 1~4
foreach ($s in 1..4) {
  & $py -s _train_v9_xuehao.py --device 0 --workers 4 --batch 16 --seed $s
  Copy-Item runs\xuehao\weights\best.pt runs\xuehao\weights\best_seed$s.pt
}

# eval 每個 seed（修改 _eval_v9_xuehao.py 的 WEIGHTS dict 逐一切換）
```

⚠ ultralytics 訓練會覆蓋 `runs/xuehao/`，記得**每次備份 best.pt** 才不會被下一個 seed 蓋掉。

---

## 7. 部署改動（雙 head 都要）

沿用之前的說明，重申一遍：

### 前處理

- **必須用環狀 warpPolar**（`R_INNER=0.6`），走 `annulus_polar()`。
- Hough + 2r ROI + white_pad 步驟同 V6.7。

### 兩 head 組合

| Head | 部署權重 | 類數 |
|---|---|---|
| 模號 | V9 mohao best.pt（此次未動）| 20 類（19 模號 + NG）|
| 穴號 | **V9 xuehao seed=0 best.pt（本次成果）** | 18 類（**注意：無 NG**）|

⚠ **不對稱注意**：V9 xuehao 是 18 類（沒有 NG class）。原本 V6.7.3 xuehao 是 19 類（含 NG）。V9 建資料時 base 用了 `data_v671/xuehao`（V6.7.2 版本，還沒加 NG），所以 V9 xuehao 不會輸出 NG。

### NG reject 邏輯

- 因 V9 xuehao 無 NG → **NG 判斷完全由模號 head 負責**。
- 若模號 head == NG → **標為不合格件，不觸發拍照**（部署層邏輯）。
- 穴號 head 在 NG 樣本上會亂輸出（因訓練沒見過），部署時**只信任模號 head 的 NG 判定**。

📝 若 dev 端希望 V9 xuehao 也有 NG，需另跑一版（把 `data_v671/mohao/NG/` 加進 build 腳本）。

---

## 8. Reproduce 資訊

如果要在 dev 端從頭複現：

### 前置（dev 端已有的檔）

- `data_v671/xuehao/{train,val}/01..18/*.jpg`
- `data3/M17/01..18/*.jpg`（721 張原相機圖）
- `data4/M17/01..18/*.jpg`（361 張，未見探針用）
- `yolov8s-cls.pt`（13 MB）
- `OCR/yolo_a_V6/v6_preprocess.py`
- `OCR/yolo_a_V6.7/v67_dataset.py`
- `OCR/yolo_a_V6.7.1/v671_aug_ops.py`
- `OCR/yolo_a_V6.7.3/v673_dataset.py`
- `OCR/yolo_a_V6.7.3/runs/xuehao/weights/best.pt`（baseline 對照）

### 執行

```powershell
$py = "C:\Users\<user>\anaconda3\envs\lens-gpu\python.exe"

# Step 1
& $py -s OCR\yolo_a_V9\_build_data_v9_xuehao.py
# 預期產出：data_v9/xuehao/{train:6482, val:1232}

# Step 2
& $py -s OCR\yolo_a_V9\_train_v9_xuehao.py --device 0 --workers 4 --batch 16 --seed 0
# 預期產出：OCR/yolo_a_V9/runs/xuehao/weights/best.pt（top1=99.3%）

# Step 3
& $py -s OCR\yolo_a_V9\_eval_v9_xuehao.py --device 0
# 預期產出：val 每穴號零退步表 + data4/M17 361/361=100.0%
```

---

## 9. 打包最小回傳（給送資料的人）

**主要**（必傳）：

```
OCR/yolo_a_V9/
├── HANDOFF_v9_xuehao_RESULTS.md    ← 本檔
├── _train_xuehao_seed0.log         ← 訓練 log
└── runs/
    └── xuehao/
        ├── weights/
        │   ├── best.pt              ← ★ 最重要
        │   └── last.pt              ← 可選
        ├── results.csv              ← 訓練曲線
        └── args.yaml                ← 參數記錄
```

估 **~25 MB**。

**選用**（若 dev 端要驗證 data build）：
- `data_v9/xuehao/`（~350 MB，通常不必傳，重跑 build 腳本即可）

---

## 10. 開放議題（給下一輪或 dev 端評估）

1. **09/18 微退步是否值得 seed sweep**？成本 72 min vs 收益（也許找到零退步 seed）。
2. **V9 xuehao 無 NG class**：要不要另跑一版含 NG？（此次沒做因為 base 用 v671 而非 v673）
3. **穴 06 val 只 92.4%**（v6.7.3 & v9 同樣）：跟先前分析一致 — 4 張 `exp_M28-06_got_M28-09` 是**刻意混入 M28-09 測試資料**，v9 也「答錯」表示模型正確識別內容（跟資料夾標籤不符）。此 4 張應視為「通過測試」而非真錯。
4. **穴 11 val 71/72**（v6.7.3 & v9 同）與 **穴 03 val 88/89**（同）：先前 dev 端已知的邊界問題，不在本次 M17 強化範圍。
5. **NG symmetrical 部署**：模號 V9 有 NG、穴號 V9 無 NG，AND 邏輯要調整成「只看模號」— 這已在 §7 說明。

---

_2026-07-07 GPU 訓練機出品，回傳給 dev 端接手。_
