# CRNN 策略總覽（前處理 + 策略 正典文件）

- 更新：2026-08-04。整合開發機 / 模號檢驗部署包 / 協作機(_v931_crnn_fix_回傳) 三邊已統一的最終做法。
- 定位：**讀字線**（輸出字串，如 `M82`/`12`），與 V9.x 分類線（輸出類別）並存。一顆 pipeline 同時讀模號+穴號。
- 生產入口：`D:\OCR_demo\app\ocr\crnn_engine.py`（`CrnnEngine`，介面同 OcrEngine）；`--mv CRNN` 或模型清單 CSV `前處理=crnn` 列。

---

## 一、前處理項目（逐步，含不可改常數）

```
raw 相機圖
 ①→ crop_roi(IDS_ROI)                 產線相機裁 ROI；離線圖 apply_roi=False 跳過
 ②→ find_circle (Hough 定圓)           失敗→整圖 white_pad_square 當 strip，hough_used=False
 ③→ 裁 2r 方形 ROI + white_pad_square   以圓心裁正方形、白補到 2r×2r
 ④→ annulus_polar                      warpPolar 極座標展開 → 只留外圈 [0.6r, r] 字環
                                        （丟內圈=治鏡片花紋 shortcut）→ transpose+flip
                                        → white_pad_square → 640×640 strip（單 pass，不做 p90）
 ⑤→ crop_band                          strip[280:360] → 80×640 文字帶
 ⑥→ wrap crop（接縫跨界接回）           以 detector 框中心 cx 裁 ±100px：
                                        crop = band[:, np.arange(cx-100, cx+100) % W]
                                        負索引/超界索引經 % W 繞到 strip 另一端
                                        → 被 0°/360° 接縫劈開的字自動拼回完整（治 got_M8 病）
 ⑦→ to_tensor                          /255 → CHW → (x-0.5)/0.5 ∈ [-1,1]，200×80 crop
```

**不可改常數**（與訓練一致，改了 train/infer 不一致直接崩）：

| 常數 | 值 | 出處 |
|---|---|---|
| strip 尺寸 | 640×640 | `crnn_dataset` |
| R_INNER | 0.6 | 同 V6.7 環狀，`不可改` |
| 文字帶 | rows [280, 360]（高 80） | BAND_TOP/BOTTOM |
| HALF_W | 100（crop 寬 200） | 訓練 crop 尺寸 |
| DET_CONF | **0.10** | 碎片框信心 0.34~0.73、淺印整框也偏低；0.5 會兩頭堵。11,350 張實測零 detect-miss 零誤框 |

---

## 二、CRNN 策略

### 2.1 架構
```
strip ─→ detector(YOLOv8n, 2類: cls0=模號框 / cls1=穴號框, conf=0.10)
      ─→ roll-pass 幾何仲裁（見 2.2）決定每 head 的 cx
      ─→ wrap crop ±100 % W → 兩個 200×80
      ─→ Non-AR OCR（4 個 learnable query × cross-attention × 2 層,
                     每 position 獨立分 12 類: <blank>,M,0-9）
      ─→ decode_padded → (模號字串, 穴號字串) + per-char 信心
```
- **為什麼 Non-AR 不是 CTC-CRNN/attention decoder**：兩者有 sequential vocab bias（訓練沒看過的字元組合如 M54 讀 0%）；Non-AR 每位置獨立、無先後條件 → 唯一破 vocab bias 的架構（協作機 14 版實驗結論）。
- **無 NG 類**：訓練 skip_ng=True。不良品/不確定靠 `needs_review`（任一 head 信心 < 門檻）送人工；「兩 head 皆 NG 才拒收」邏輯對 CRNN 不適用。
- 信心定義：解碼字元中**最低**的 softmax 機率（最弱字決定可信度）。

### 2.2 roll-pass（接縫保險絲，交接檔 §5）
```
detector 跑 原圖 + np.roll(strip, W//2) 各一次（可同 batch）
每 head：兩 frame 候選按 (edge_dist, det_conf) 取大者   ← 幾何完整性優先於信心
B-frame 座標映射回原圖：(cx - W//2) % W
用「原圖」strip wrap crop 讀字
```
- 防的是：**detector 只框到接縫碎片**（碎片可能高信心讀成別的字）。roll 半圈後該字必完整，框會更「離邊緣遠」→ 幾何否決碎片框。
- 成本 +1 次 detector（~13ms）。兩 frame 都無框 → 回 `?` + needs_review（安全失敗）。
- **pass 策略：單次前處理 + roll-pass**（交接檔 §4③）：2-pass(p0+p90) 對比僅 +0.035%（5675 對差 2 對、皆非接縫型）→ 用 roll-pass 取代 p90 整條前處理+偵測+推論，換一倍吞吐。

### 2.3 權重（現行 = v4）
| 元件 | 檔案 | md5 前8 |
|---|---|---|
| detector | `models/crnn/runs/detector/weights/best.pt`（07-19 訓，252 手標，未再動） | a5fe4161 |
| **Non-AR v4** | `models/crnn/runs/nonar_v931_fix_v4/best.pt` | **d6b161b6** |

沿革：`include_M54`(07-20 初版) → `v931_fix v1~v3`(v9.3 的 896 錯 rehearsal，前處理區 5675 對修到 100%) → **v4**(+M82 現場淺印 72 crops)。rehearsal 池 = 1905 crops 全真實錯誤圖，無合成。

### 2.4 實測數據（v4）
| 守門 | 結果 |
|---|---|
| stock val 2416 | 99.92% |
| 前處理區 5675 對（unseen 98.4%） | **99.98%**（唯 1 錯 M28-15→16） |
| M83 1350 對（M82 對撞風險區） | **100%**（零對撞） |
| 現場錯誤批 29 張（產線讀字 0/29） | 25/29；**M82 淺印/接縫 20/20** |
| 本機 production engine 覆驗（roll-pass 版） | M82 20/20、乾淨 raw 12/12、p90 latency 65ms |

### 2.5 新錯誤 SOP（交接檔 §6，四輪驗證過）
1. 收錯誤 raw/strip；**可疑標籤先剔**（兩系統一致讀出非 gt 值 → 人工覆核）
2. `_extract_crops_from_errors.py` / `_extract_m82_field.py` 抽 crops 進 rehearsal 池
3. 複製最新 train script 改 RUN_DIR → 30 ep（~12 分鐘）
4. **三關守門**：stock val / 前處理區全量（**盯混淆對**：推 M82 盯 M83、推 M28 盯 M23）/ 原錯誤批重測
5. 舊 run 不覆蓋，可回滾
- 原則：真槓桿是**字元×位置覆蓋**非張數；模糊批（如 M15）不進訓——影像品質問題先解影像。

### 2.6 已知邊界（交接檔 §7）
1. **wavy-band 幾何走位**：Hough 圓心偏 → 字帶偏出 [280,360] → detector 無框。roll/conf 救不了；roll-pass 後仍缺 head = needs_review → 重拍。
2. **M15 模糊批**：影像品質問題（6/9 為淺印特徵 spillover 順帶救的），不進訓。
3. **前處理區無 M82 料** → M82 長期守門靠 stock val(55) + 現場回饋持續送。

---

## 三、三邊統一狀態（2026-08-04 核驗）

| 機制 | 開發機 live | 模號檢驗部署包 | 協作機 |
|---|---|---|---|
| detector | ✓ a5fe4161 | ✓ 同 | ✓ 同顆 |
| wrap `% W` | ✓ | ✓（engine md5 一致 b6d16ebc） | ✓ |
| conf 0.10 / roll-pass / v4 | ✓ | ✓ | ✓ |

⏭ **產線實體機仍是舊副本（pre-07-28，無 wrap = got_M8 根源）→ 增量包(app/ + models/crnn/ + 模型清單.csv，~45MB)重新打包搬機後四邊一致。**

## 四、檔案地圖
- 生產 engine：`D:\OCR_demo\app\ocr\crnn_engine.py`；設定 `app\config.py`（CRNN_* 區）
- 可攜包（他機執行）：`D:\OCR_demo\models\crnn\`（含 `crnn_infer.py` 獨立入口 + `交接_他機部署.md`）
- 訓練/資料建置：`d:\Content_lens_OCR\OCR\crnn_fallback\`（`_train_nonar*.py`、`_extract_*`、手標工具；架構決策史見 `HANDOFF.md`）
- 協作機回傳（v4 出處 + 17 支實驗腳本 + 11 份報告）：`D:\income\_v931_crnn_fix_回傳_2026-08-03\`
