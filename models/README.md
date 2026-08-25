# models/ — 模型資產索引

> 匯入日：2026-08-25。本目錄是**唯一備份**：來源的 `Content_lens_OCR` repo
> (`la0221/Contact_Lens_DRI_System`) 對這些檔案 **`git ls-files` 全為 0**，匯入前是單點無備援。

## 目錄

| 路徑 | 是什麼 | 來源 |
|---|---|---|
| `published/ocr_crnn/b3/` | **產線實際載入**的 CRNN（sidecar 讀這份） | `D:\AIVisionModels\ocr_crnn\b3`（模型中樞） |
| `training/crnn_fallback/` | CRNN 訓練/評估端：資料集、訓練腳本、run 產物、報告 | `D:\Content_lens_OCR\OCR\crnn_fallback` |
| `training/yolo_a_V9/` | v9 / v9.1 / v9.2 / v9.2n 四個 run（模號＋穴號） | `D:\Content_lens_OCR\OCR\yolo_a_V9` |
| `training/yolo_a_V9.2/` | v9.2 獨立目錄（模號） | 同上 |
| `training/yolo_a_V9.3/` | v9.3 穴號 ＋ `m28_newfont_holdout` 留出集 | 同上 |
| `training/yolo_a_V9.4/` | v9.4 模號＋穴號，**含已匯出的 `.onnx`** | 同上 |

## ★ md5 溯源：b3 的來源已可證

ROADMAP 主項 1 長期卡在「版本漂移收斂」—— `pairs\*` 大多查不到 Content_lens 來源。
CRNN 這條**不一樣，逐檔對得上**：

| 產線檔（b3） | md5 | 訓練端對應 run |
|---|---|---|
| `detector.pt` | `a5fe4161b862a764136849f108b031d8` | `crnn_fallback/runs/detector/weights/best.pt` |
| `nonar.pt` | `2daeeb4e04a44ac071e9206bc20a5fc4` | `crnn_fallback/runs/nonar_v931_fix_v3/best.pt` |

兩者 md5 **完全相同**，且與中樞 `_publish.json` 登錄值一致 →
**b3 = detector + nonar_v931_fix_v3，訓練端到產線全鏈路可追溯。**
（`runs/nonar_include_M54/best.pt` = `7e9e8111…`，是另一個候選，**未上線**。）

## yolo_a_V9 系列權重 md5

| run | 檔案 | md5 |
|---|---|---|
| V9 / mohao | `best.pt` | `1ae94a6c4c897c0547ccbf037506da70` |
| V9 / xuehao | `best.pt` | `b389ebf5b59a7d94206c61ea88f3336b` |
| V9 / v91 mohao | `best.pt` | `89df430cdad12a09a9b8360a8e2a202d` |
| V9 / v92 mohao | `best.pt` | `3a4fb07fb5bcdfff9dcd0e2b432acf4d` |
| V9 / v92 xuehao | `best.pt` | `4e79bb874596a3e39e0523e19c457c07` |
| V9 / v92n xuehao | `best.pt` | `814518ea164c432c3594a927068d3612` |
| V9.2 / mohao | `best.pt` | `46b13b658d5d756533217a02adb29c08` |
| V9.3 / xuehao | `best.pt` | `e6d5b1a7ff0e965f8283d317a5e203f5` |
| V9.4 / mohao | `best.pt` | `f550fc970d9448d4bf71500e5681c280` |
| V9.4 / mohao | `best.onnx` | `f241c90f647742db6dea012d20daeeb5` |
| V9.4 / xuehao | `best.pt` | `3ff7378c556f2bd60e449580b19ec849` |
| V9.4 / xuehao | `best.onnx` | `58af15b0b550ff7a5ab427a42f07e65b` |

驗證：`find models -name "*.pt" -o -name "*.onnx" | sort | xargs md5sum`

## 匯入時刪掉了什麼（可重生，不是遺漏）

| 刪除項 | 檔數 | 省下 |
|---|---|---|
| `last.pt`（末代 checkpoint，只用於續訓；`best.pt` 全部保留） | 11 | 104 MB |
| `diag/` 診斷影像輸出 | 72 | 9.8 MB |
| `val_batch*.jpg` 驗證抽樣圖 | 64 | 6.9 MB |
| `train_batch*.jpg` 增強抽樣圖 | 69 | 6.7 MB |
| `yolo26n.pt` ultralytics 預訓練底模（官方可下載，且重複兩份） | 2 | 10.6 MB |
| `__pycache__/`、`*.pyc`、ultralytics `*.cache` | 16 | 0.2 MB |

**保留**：所有 `best.pt` / `best.onnx`、`args.yaml`（超參，重現用）、`results.csv`、
`results.png`、`confusion_matrix*.png`、完整 `_train_*.log`、
`detector_data/`（252 張圖＋252 個 YOLO 標註，人工標註成果，無法重生）、
`rehearsal_crops_sample/`（排練集樣本 —— 交接檔載明是防災難性遺忘的唯一防線）、
`m28_newfont_holdout/`（V9.3 留出評估集）、各 `HANDOFF*.md` 與 `reports/`。

362 MB → **224 MB**，最大單檔 19.5 MB（遠低於 GitHub 100 MB 上限，不需 Git LFS）。

## 注意

- 本目錄是**快照**，不是模型中樞本身。中樞治理（版本不可變、`_publish.json` 溯源、
  md5 複驗、`/api/models` 發布流程）仍以 `D:\AIVisionModels` 為準，見 `ROADMAP.md` 主項 1。
- `pairs/*`（舊 ocr_pair 雙 head，8 個版本共 292 MB 高度重複）**未匯入** ——
  引擎策略已拍板由 CRNN 逐步取代，且該系列正是「查不到來源」的那批。
