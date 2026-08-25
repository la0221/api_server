# V9.3 交接清單

日期：2026-07-15
負責版本：**V9.3**（**只有穴號**；模號沿用 v9.2）
修的問題：**M28-04「新刻印字體」被讀成 06**（v9.2 在該批 42% 誤判）

---

## 〇、先讀這段（避免重蹈本 session 的彎路）

| 觀念 | 正確認知 |
|---|---|
| 版本主線 | **V9 系列＝通用模型**（單一模型、免路由）。V6.7.x 是舊的「逐模號穴號 fine-tune」路線，**已不是主線** |
| 訓練方式 | **V9 的 A 軸＝全量從頭重訓（`yolov8s-cls.pt`），不 warm-start**（warm-start 會遺忘，見記憶 `retrain-full-not-warmstart`） |
| 現行部署 | `MODEL_VERSIONS["V9.2"] = (v9.2 模號, v9.2n 穴號)`（OCR_demo config 已註冊） |
| V9 的本質限制 | 通用版＝**整體環外觀分類器**：**外觀脆弱** + **根本不讀字**（靠紋理 shortcut） |
| 這次問題的定位 | **不是 v9.2 壞掉**，是遇到**沒見過的新字體** → 記憶早已預言的「舊模號新外觀→必須餵該機台圖，增強救不了」**打地鼠** |

---

## 一、問題現象

`錯誤M28-04/`（981 張，全 M28-04）分三個子夾＝三顆不同實體模具：

| 子夾 | 張數 | 誤判 | 錯誤率 |
|---|---|---|---|
| **M28_2** | 316 | **132** | **41.8%** ← 錯全在這 |
| M28_3 | 317 | 0 | 0% |
| m28-4 | 348 | 0 | 0% |

- 誤判內容：**M28-04 → M28-06（128）**、→01（3）、→05（1）。**全是穴號錯，模號 M28 全對** → 只需修穴號。
- 肉眼原因：**M28_2 的字刻在「凸起方框塊」上、粗體有外框**；M28_3/m28-4 是**直接淺刻細字、無框** → 字體/刻印工藝不同 = 新外觀。

---

## 二、新增檔案（`OCR/yolo_a_V9.3/`）

| 檔案 | 用途 |
|---|---|
| `_build_v93_xuehao.py` | 建 `data_v93/xuehao`＝data_v92n/xuehao 複製 + M28_2 新字體進穴號04 |
| `_train_v93_xuehao.py` | 穴號全量重訓（比照 `_train_v92n_xuehao.py`） |
| `_eval_v93_xuehao.py` | 三道守門 |
| `_train_v93_xuehao.log` / `_eval_v93.log` | log |
| `m28_newfont_holdout/` | **36 張新字體 holdout（未進訓練）** |
| `runs_v93/xuehao/weights/best.pt` | **本版權重** |
| `HANDOFF_v93.md` | 本檔 |

---

## 三、結果（守門全過）

| 守門 | 結果 |
|---|---|
| ① 新字體 holdout（36，未進訓練） | **04 讀對 36/36 = 100%**（原 42% 誤判） |
| ② 舊字體 M28_3+m28-4（665） | **665/665 = 100%**（沒弄壞） |
| ③ data_v93 val 每類 | 總 **1261/1268 = 99.45%**；04 **103/103**；**NG 6/6 保住** |
| 吸收槽檢查 | **✓ 無任何類被吸去 04**（雖 04=1284 為其他類 3.7×） |

殘留（既有、非本次造成）：**穴06 93.9%**（v9.2n 亦 93.9%，記憶標為「下個既有弱點」）、03/11/16 各差 1 張。

---

## 四、配方（照 V9 A軸 SOP）

```
init      : yolov8s-cls.pt（全量從頭；log 顯示 Transferred 156/158 ＝非 warm-start）
data      : data_v93/xuehao（19 類：01–18 + NG）
dataset   : XuehaoMixedTierDataset（來自 OCR/yolo_a_V6.7.3/v673_dataset.py）
前處理    : 環狀 warpPolar R_INNER=0.6（OCR/yolo_a_V6.7/v67_dataset.py）
tier      : tier1 只旋轉不做外觀抖動 / NG=tier3 heavy
超參      : AdamW lr0=5e-4, lrf=0.1, cos_lr, warmup 0, epochs 20, batch 16, imgsz 640,
            patience 8, seed=0, deterministic=True；ultralytics 內建 aug 全關
輸出      : OCR/yolo_a_V9.3/runs_v93/xuehao/weights/best.pt
```

### 資料組成（`data_v93/xuehao`，train=7634 / val=1268）
- 基底：`data_v92n/xuehao` 全複製（19 類含 NG）
- 加：`錯誤M28-04/M28_2` 316 張 → **250 train / 30 val / 36 holdout**（seed 0 shuffle，前綴 `m28nf_`）
- 穴號04 train：1034 → **1284**

---

## 五、重跑步驟

```powershell
$py = "C:\Users\<user>\anaconda3\envs\lens-gpu\python.exe"
cd OCR\yolo_a_V9.3
& $py -s _build_v93_xuehao.py                       # 建 data_v93/xuehao
& $py -s _train_v93_xuehao.py --device 0 --workers 4
& $py -s _eval_v93_xuehao.py --device 0             # 三道守門
```

相依：`data_v92n/xuehao`、`錯誤M28-04/M28_2`、`yolov8s-cls.pt`、
`OCR/yolo_a_V6.7/v67_dataset.py`、`OCR/yolo_a_V6.7.3/v673_dataset.py`、`OCR/yolo_a_V6/v6_preprocess.py`

---

## 六、部署（**尚未做**）

比照 v9.2n 的做法：

1. 複製權重到部署樹：
   `OCR/yolo_a_V9.3/runs_v93/xuehao/weights/best.pt`
   → `D:\OCR_demo\Contact_Lens_DRI_System\yolo_a_V9.3\runs_v93\xuehao\weights\best.pt`
2. `D:\OCR_demo\app\config.py` 新增：
   ```python
   V93_DIR = REPO / "yolo_a_V9.3"
   V93_XUEHAO_WEIGHTS = V93_DIR / "runs_v93" / "xuehao" / "weights" / "best.pt"
   MODEL_VERSIONS["V9.3"] = (V92_MOHAO_WEIGHTS, V93_XUEHAO_WEIGHTS)   # 模號沿用 v9.2
   ```
3. 啟動：`python app/main.py --source ids --live --expect M28/04 --mv V9.3 --passes 2`
4. 線上前處理已是環狀 0.6 + 2-pass（`app/ocr/preprocess.py` R_INNER=0.6、`MULTI_PASS=2`、`EARLY_EXIT_CONF=None`）→ **與訓練一致，不用改**

---

## 七、Caveat（必讀，別過度樂觀）

1. **holdout 是同場次**（M28_2 同一批 316 張切出來的 36）→ 只證「**餵了學得會**」，**不證跨場泛化**。真考在驗證機。
2. **這仍是打地鼠**：下一個新字體 / 新機台 / 新打光還是會崩。已量化證據：v9.2 純靠增強做域隨機化，M28 holdout 只有 **19.2%**（v9.1 餵圖=100%）→ **增強補不出結構性外觀差**。
3. **治本仍是 S5＝偵測/OCR 讀真正壓印字**（S5 可讀性探針：同批失敗圖分類器 0% vs OCR 讀出 M28 84%）。穴號是 S5 主要工程量（穴號11 現成 OCR 僅 25%，多半是 rim 字帶傾角/裁切問題）。
4. 穴06（93.9%）是**下一個既有弱點**，本次未動。

---

## 八、下一步建議

| 優先 | 事項 |
|---|---|
| 高 | **部署 V9.3 到驗證機實測**（holdout 同場次不算數，要真跨場） |
| 高 | 收 **M28_2 這顆模具在不同場次/打光** 的圖，測跨場泛化 |
| 中 | 穴06（93.9%）補現場圖 |
| **最高（治本）** | **S5 rim 讀字器**：偵測字帶 → 環幾何/06-09 底線轉正 → 緊裁高解 → 辨識（含 6/9）。v9.2 的 19.2% 就是「該投 S5」的證據 |

---

## 九、本 session 的踩坑（給下一棒）

1. **別把 V6.7.x 當主線** — 我一開始拿 V6.7.2+V6.7 當最佳，其實使用者早在 V9.2。
2. **別 warm-start** — V9 是全量重訓。
3. **別亂建 `data_v67x/`** — 使用者的 `_build_data_v673.py` 依賴 `D:\incoming\M17`（本機沒有），我曾憑空建出缺 NG/M17 的錯誤 `data_v673/xuehao`（已清除）。
4. **junction 只能 `os.rmdir`**，用 `shutil.rmtree` 會刪到 `data_v65` 本體。
5. **寫檔前先 Read** — `OCR/yolo_a_V6.7.1/` 與 `V6.7.3/` 都已有使用者自己的完整實作，差點被覆蓋。
