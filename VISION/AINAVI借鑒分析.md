# AINAVI 借鑒分析 — 對應 Vision 三專案（opencv_vision / VisionFlow / yolo_service）

> **建立日期**：2026-06-12
> **目的**：盤點 Spingence AINAVI（`C:\Program Files\Spingence\AINavi`）有哪些**設計概念 / 訓練配方 / 開源庫選型**值得借鑒，分別餵進我們的 Vision 三專案來優化。
> **定位（紅線）**：**不破解、不反編譯它的 `.pyd`、不抽取它的專屬權重（`_w`/`.bin`）**。本文只借三類**可光明正大檢視**的東西：①架構設計（目錄結構 / block 分類 / C API）②可讀的訓練配方 schema（`train.json`）③它選用的開源庫（我們自己裝官方版）。
> **下一步**：先補充 **opencv_vision**（見 §3）。

---

## 0. AINAVI 全貌速覽

| 項目 | 內容 |
|---|---|
| 本質 | Spingence 商用 AI 視覺軟體，Python 3.9 打包，核心編譯成 `.pyd` |
| 架構 | `training_server` / `inference_server` / `workflow` 三服務 + 對外 **C DLL API**（附 `.h` 標頭） |
| 底層框架 | **PyTorch 2.8 + CUDA 12.8**、onnxruntime-gpu 1.19.2、torchvision |
| CV 庫 | **OpenCV 4.11**（`opencv_world4110.dll`，給 template match）、**kornia 0.8.0**、shapely 2.0 |
| 演算法目錄 | `score/plugins/`：ano_1, ano_2, cls_1, det_1s, ocr_2, seg_1, seg_2（+ dummy/cus_1） |

### 演算法身分（從目錄結構 + train.json + 權重命名還原）

| Plugin | 推測架構 | 用途 | 我們現況 |
|---|---|---|---|
| **det_1s** | YOLO 系單階段（`darknet`+`backbone`+`head`，320×640，SGD） | 物件偵測 | ✅ yolo_service 已有，模型同源 |
| **cls_1** | CNN 分類（150×150，backbone freeze 遷移學習，NLLLoss） | 分類 | ✅ 已有 |
| **ano_1** | 特徵嵌入式異常（`anomaly_core`+`center`+`rp`隨機投影+`k=5` kNN）≈ **PaDiM/PatchCore**，3 種尺寸 67/147/263MB | 無監督瑕疵檢測 | ❌ **沒有** |
| **ano_2** | 重建/student-teacher 路線（`med`+`ProgressiveTransform`，10MB+111MB）≈ **EfficientAD** 類 | 無監督瑕疵檢測 | ❌ **沒有** |
| **ocr_2** | **PP-OCR/CRNN 文字辨識**（48×320=PaddleOCR rec 標準輸入，CTC，附 en 預訓練） | 字元辨識 | ⚠️ 模號OCR 進行中 |
| **seg_1 / seg_2** | 語意分割（U-Net 類，Dice loss；seg_1 256×256 輕 / seg_2 320×640 重） | 分割 | 部分有 |
| **template_match** | 傳統 OpenCV 形狀/邊緣比對（`PatternMatchDLL`） | 定位/對位 | ✅ 已有 |

---

## 1. 主對應表 — 哪個部分對應什麼

> 一眼看「AINAVI 的什麼」→「借給哪個專案」→「借的是概念還是程式碼」。

| # | AINAVI 來源 | 借鑒內容 | 對應專案 | 類型 | 優先 |
|---|---|---|---|---|---|
| A1 | `kornia/feature/`（LoFTR, LightGlue, DISK, DeDoDe, SOLD2, AdaLam） | SOTA 影像對位/特徵匹配演算法 | **opencv_vision** | 開源程式碼（自裝官方） | 🥇 |
| A2 | `workflow/entities/transformations/`（static_crop / dynamic_crop / image_transform） | 前處理「transform 單元」化 | **opencv_vision** | 設計概念 | 中 |
| A3 | `PatternMatchDLL` + OpenCV | 傳統比對保留 + 難件 fallback 深度對位的雙層策略 | **opencv_vision** | 設計概念 | 中 |
| B1 | `workflow/entities/` 的 block 四大分類（input/transformations/models/logic） | 節點體系分類法 | **VisionFlow** | 設計概念 | 🥈 |
| B2 | `dynamic_crop` block | 用上游結果（偵測框）動態裁切 ROI 餵下游 model | **VisionFlow** | 設計概念 | 🥈 |
| B3 | `model_filter` block | 判定邏輯也做成 block，pipeline 全 JSON 可組態 | **VisionFlow** | 設計概念 | 中 |
| B4 | `graph_constructor`/`graph_parser` + `visualize_workflow_runner` + `GetWorkflowGraph`(回傳 PNG) | graph 建構/解析分離 + 流程圖可視化 | **VisionFlow** | 設計概念 | 中 |
| B5 | C API：`CreateWorkflow(id)`/`SetCurrentWorkflow`/`ListWorkflowIds` | 一個 process 管多條 pipeline、id 切換 | **VisionFlow** | 設計概念 | 中 |
| C1 | `det_1s/train.json` 的 `Freeze` callback（epoch 30 凍 backbone 前 40 層） | Backbone 漸進解凍訓練策略 | **yolo_service** | 訓練配方 | 🥉 |
| C2 | `ProgressiveTransform`（start/stop ratio） | 漸進式資料增強（初期弱、後期強） | **yolo_service** | 訓練配方 | 🥉 |
| C3 | `train.json` augmentation schema（7 項各自獨立範圍/機率） | 訓練設定檔 schema 範本 | **yolo_service** | 訓練配方 | 中 |
| C4 | det_1s 輸入 320×640（非正方形） | 寬幅料件用非正方形輸入省算力 | **yolo_service** | 設計概念 | 低 |
| C5 | torch → ONNX → onnxruntime-gpu 部署 | 印證 ONNX 部署選型 | **yolo_service** | 設計概念（佐證） | 低 |
| D1 | `ano_1`/`ano_2`（PatchCore/EfficientAD 路線） | 「只用良品訓練抓缺陷」無監督瑕疵檢測 | **三專案未來共同缺口** | 開源能力（走 anomalib） | 待規劃 |

---

## 2. opencv_vision 補充清單（下一步重點）

> **現況**：C# / .NET8 WPF + Akka Actor + **OpenCvSharp4 4.10**，多相機 TCP/IP。**目前無 ONNX runtime**。
> **痛點**：對位 / 校正 / J matrix / PMF / fiducial，傳統 template match 遇反光、低對比、形變會掉。

### 2.1【🥇 A1】kornia 對位演算法 — 最高價值

> ✅ **2026-06-12 spike 已驗證**：真實 M101 反光件上 DISK+LightGlue 對位命中率 **100%**（精度 <0.5px），是唯一撐過「反光+模糊+低對比+30°」最壞組合的方法；現行 ORB 在該組合 0/4 全滅。深度對位**值得整合為難件對位主力**。詳見 [research/deep-anchor-spike/REPORT.md](research/deep-anchor-spike/REPORT.md)。限制：重複圖樣場景仍是 template/NCC 贏 → 採雙層。
>
> ✅ **2026-06-12 落地完成（Path B）**：Python sidecar [VISION/DeepAnchorService](../DeepAnchorService/) + C# 整合（Core `DeepAnchorSolver` 對應點→AnchorFrame、Engine `DeepAnchorHttpClient`、Host appsettings `DeepAnchor` 段）。Codex hostile review 1 輪、3 finding 全修（共線 gate / scale 0.05 / options 驗證）。測試 Core 209/209、Vision 187/187、真實 M101 端到端通過。**下一步**：Host DI 把 client 接進 `VisionItemTreeExecutor` base-anchor 定位（難件走深度對位）+ 真實站台 taught/live 驗證。

可借的 SOTA 對位演算法（全部開源、與 AINAVI 無關，我們自己裝官方版）：

| 演算法 | 特性 | 適用我們場景 |
|---|---|---|
| **LoFTR** | 免特徵點稠密對位，反光/低紋理也能配 | 低對比料件對位 |
| **LightGlue** | SOTA 特徵匹配，**有 ONNX 匯出**（kornia 內含 `lightglue_onnx`） | 一般 fiducial 對位、可部署 C# |
| **DISK / DeDoDe** | 學習式特徵點偵測 | 取代手工 corner/SIFT |
| **SOLD2** | 線段偵測與匹配 | 直邊/輪廓對位 |
| **AdaLam** | 幾何驗證濾錯誤匹配 | 提升匹配 robust |

**整合路徑（重點 — opencv_vision 是 C#，不能直接 pip kornia）**：

- **路徑 A（推薦，純 C#）**：把 LightGlue / LoFTR **匯出 ONNX**（kornia 已附 `lightglue_onnx`）→ opencv_vision 加 `Microsoft.ML.OnnxRuntime`(.Gpu) NuGet → C# 內原生跑，無 Python 依賴。
- **路徑 B（Python sidecar）**：仿 yolo_service 模式，把 kornia 對位包成 Python FastAPI 服務，opencv_vision 走 TCP/HTTP 呼叫。重對位場景才打。
- **建議**：先做 **spike** — 拿現有難對位料件，LoFTR/LightGlue 跟現行 template match 比命中率，確認有效再決定 A 或 B 落地。

### 2.2【A3】雙層對位策略

傳統 template match（OpenCvSharp）**保留為主**（快、可解釋）；**難件才 fallback** 到 kornia 深度對位。形成「傳統優先、難件深度補強」分層，不要一次全換。

### 2.3【A2】前處理 transform 單元化

把散落的 OpenCV 前處理呼叫收斂成可重用、可組態的 transform 單元（對應 AINAVI 的 `image_transform`/`static_crop`/`dynamic_crop`），方便跟 VisionFlow 的 block 體系對接。

---

## 3. VisionFlow 借鑒清單

> **現況**：AI 視覺流程編輯/檢測系統，MVP 階段。AINAVI 的 workflow 引擎架構是**最直接可抄的設計**（抄概念與分類法，不抄碼）。

### 3.1【🥈 B1】Block 四大分類法

照搬這個節點分類體系：
```
input          → workflow_input_block, workflow_image
transformations→ static_crop, dynamic_crop, image_transform
models         → classification, object_detection, segmentation, anomaly
logic          → model_filter, template_match
```

### 3.2【🥈 B2】dynamic_crop — 串接核心

用上游 block 的結果（如偵測框）**動態裁切 ROI** 餵給下游 model。這是「**偵測 → 裁切 → 分類/OCR**」多階段流程的關鍵 pattern（正是模號OCR分料那種場景要的）。

### 3.3【B3】model_filter — 判定邏輯 block 化

把判定/過濾邏輯也做成 block 節點，而非寫死程式 → 整條 pipeline JSON 可組態。

### 3.4【B4】graph 建構/解析分離 + 流程圖可視化

- `graph_constructor` / `graph_parser` 分離（JSON ↔ graph）
- 兩種 runner：`workflow_runner`（正式跑）/ `visualize_workflow_runner`（出可視化）
- `GetWorkflowGraph` 直接回傳 PNG 流程圖 → **VisionFlow 編輯器可視化**可學這招

### 3.5【B5】多 workflow 實例管理

C API 揭露：`CreateWorkflow(id)` / `SetCurrentWorkflow` / `ListWorkflowIds` → 一個 process 同時掛多條 pipeline、用 id 切換；輸入支援 binary / packed-uint32 / file 三種餵法。多站多流程並存可參考。

---

## 4. yolo_service 借鑒清單

> **現況**：Python FastAPI YOLO 訓練/推論服務，RTX 4090。`det_1s` 與我們同源（darknet/YOLO 系），**模型架構沒新東西，值得借的是「訓練配方工程」**（`det_1s/train.json` 可讀）。

| 借鑒點 | det_1s 做法 | 餵進我們 YOLO |
|---|---|---|
| **【C1】Backbone 凍結** | `Freeze`: epoch 30 凍 backbone 前 40 層 | 先學 head 再放開，小資料集更穩 |
| **【C2】漸進式增強** | `ProgressiveTransform` start/stop ratio | 初期弱增強、後期強增強 |
| **【C3】完整增強 schema** | brightness/contrast/hue/saturation/rotation/shift/shear 各自獨立範圍與機率 | 當訓練設定檔 schema 範本 |
| **【C4】非正方形輸入** | 320×640 | 寬幅料件省算力、不變形 |
| **【C5】部署路線** | torch → ONNX → onnxruntime-gpu 1.19.2 | 佐證 ONNX 部署選型正確 |
| LR 排程 | stepLR step=40, gamma=0.6 | 對照現用排程 |

---

## 5. 共同未來缺口：無監督異常偵測

三專案目前**都沒有**「只用良品訓練、抓沒見過缺陷」的能力。AINAVI 用 `ano_1`(PaDiM/PatchCore) + `ano_2`(EfficientAD) 兩路覆蓋。

**借鑒方式**：**不依賴 AINAVI**，走開源 **`anomalib`**（PatchCore / PaDiM / EfficientAD 都有官方實作）。未來要做瑕疵檢測時，可在 yolo_service（Python）起一條 anomalib pipeline，或包成獨立服務供三專案呼叫。

---

## 6. 優先順序與下一步

| 優先 | 動作 | 專案 | 風險/成本 | 回報 |
|---|---|---|---|---|
| 🥇 | kornia LoFTR/LightGlue 對位 spike（先比命中率，再決定 ONNX/sidecar 落地） | opencv_vision | 低 | 高（解老痛點） |
| 🥈 | 抄 block 四分類 + dynamic_crop/model_filter | VisionFlow | 中（重構） | 高（架構升級） |
| 🥉 | 移植 det_1s 訓練配方（freeze / progressive aug / schema） | yolo_service | 低 | 中 |
| 待規劃 | anomalib 補無監督異常偵測能力 | 三專案共用 | 中 | 高（新能力） |

**→ 已定下一步：先補充 opencv_vision（§2，從 🥇 kornia 對位 spike 起手）。**

---

## 7. 商業模式與擴充封閉性（2026-06-19 實地盤點）

> 補 §0：之前只盤點演算法槽位，這次實地查安裝目錄 `C:\Program Files\Spingence\AINavi` + runtime config `C:\Users\Public\Documents\Spingence\AINavi` + 使用手冊，補上**授權/商業模式**與**擴充是否開放**兩塊。
> **守紅線**：未反編譯 `.pyd`、未抽權重；結論只依目錄結構 / 副檔名·大小 / 可讀 `.h`·`.json`·`.py` / PE 檔案屬性。

### 7.1 商業模式 — 授權 / 打包 / 閘控（機制完整可見，定價不在磁碟）

| 面向 | 內容 | 證據 |
|---|---|---|
| 授權三後端 | `order=[computer_id, dongle, unlimited]`，依序 fallback | `config\license.json` |
| computer_id | 軟體綁機器指紋（**本機用這個**），活化態加密存 registry | `sp_license\plugins\computer_id.pyd`、`tools\{cipher,registry}.pyd` |
| dongle | Thales/SafeNet **Sentinel HASP USB dongle**，vendor code 96033 | `sp_license\lib\dongle\hasp_windows_x64_96033.dll`（PE: SafeNet / Sentinel LDK） |
| dongle_free / unlimited | 軟體 node-lock 工具 / dev 不限制 bypass | `DongleFreeLicenseTool.exe`（Spingence 自家）、`plugins\unlimited.pyd` |
| 計量授權 | `Training license:N` / `Inference license:N` + `Valid until` | 手冊 p035（Cloud / Dongle / Device ID 三種啟用） |
| EdgeHub = 控制平面 | 授權邏輯**只在** EdgeHub，對外 `/api/license` REST，負責 launch / verify / heartbeat 各 model service | `AINavi_Edgehub\src\{service\license.pyd, use_cases\license_use_cases.pyd, lib\sp_license}` |
| 模組化打包 | APP / EdgeHub / Training / Inference / Workflow Server / Auto annotation，Downloader 按角色裝（Training / Inference / Custom） | 手冊 p028；安裝目錄 top-level |
| 兩產品線 | Trainer + Inference；Trainer 訓練→push 模型到多台 Inference / 邊緣機（MIC-733/730） | 手冊 p004 / p009 / p023 |
| 源碼保護 | 每 service 吃 `--key` Secret Key，核心編 `.pyd` | `wrapper.py`「main.py will be hidden in production」 |

❌ **不在磁碟**：定價、SKU、EULA / 合約文字（只有第三方 OSS LICENSE）。商業「機制」可見，「條款 / 報價」要另找。

### 7.2 plugin 擴充 — 結構可見但封閉（GATED）

「形狀」全可見，但**第三方無法 DIY 加演算法**：

- plugin 契約：`score\plugins\<name>\` = `trainer.pyd` + `predictor.pyd` + 可讀 `train.json` + `version.txt`；有 `dummy`(最小骨架)、`cus_1`(自訂槽，命名 `cus_<n>`)
- **全部編譯**：score 樹 **111 個 .pyd vs 僅 1 個非空 .py**；`dummy` / `cus_1` 也是 `.pyd`，不是可填空 source 模板
- **cus_1 不對外**：inference + workflow 有、**training 沒有** → 自訂演算法由 Spingence 幫 build 成 `.pyd` 交付，非整合商現場寫
- **C-API 無註冊**：`ainavi_workflow.h` / `ainavi_inference.h` 只有 `CreateWorkflow(json)` / `Init` / `Inference`，**無 `Register*` / `AddBlock*` / `AddPlugin`** → workflow 擴充純 JSON 宣告式，組現成 block
- **discovery 也編譯**：`plugins\__init__.py` 全 0 bytes，dispatch 在 `predictor.pyd` / `trainer.pyd` / `block_map.pyd`；base 介面 `spipe.{Operation,Pipeline,...}` 有名字但編譯
- 唯一「開放」= 資料互通（匯入 LabelMe 4.5.6 / LabelImg 1.8.6 標註）—— 非 plugin 擴充

**對我們的意義**：能**借設計**（block 四分類、`cus_` 自訂槽的產品思路、EdgeHub + 授權架構），**不能直接擴充它**；要可擴充版本只能自建 → 正是 **VisionFlow block 體系**的定位。

---

## 附錄：AINAVI 證據路徑（可追溯，僅檢視不修改）

- 演算法目錄：`ainavi_training_server\src\lib\score\plugins\{ano_1,ano_2,cls_1,det_1s,ocr_2,seg_1,seg_2}\`
- 可讀訓練配方：各 plugin 的 `train.json`、`version.txt`
- workflow block 分類：`ainavi_workflow\src\workflows\entities\{input,transformations,models,logic}\`
- C API 標頭：`ainavi_DLLs\ainavi_workflow_dll\ainavi_workflow.h`、`ainavi_DLLs\ainavi_inference_dll\ainavi_inference.h`
- 開源庫（自裝官方版即可）：`Envs\ainavi3_base\Lib\site-packages\kornia\feature\{loftr,lightglue.py,lightglue_onnx,disk,dedode,sold2,adalam.py}`
- 傳統比對 DLL：`ainavi_DLLs\ainavi_pattern_match_dll\{PatternMatchDLL.dll,opencv_world4110.dll}`
- 授權引擎（§7.1）：`AINavi_Edgehub\src\lib\sp_license\{license_manager.pyd, plugins\{computer_id,dongle,dongle_free,unlimited}.pyd, lib\dongle\hasp_windows_x64_96033.dll, lib\DongleFree\DongleFreeLicenseTool.exe, tools\{cipher,registry}.pyd}` + `AINavi_Edgehub\src\service\license.pyd`
- runtime 授權設定（§7.1）：`C:\Users\Public\Documents\Spingence\AINavi\config\{license.json, machine.json}`
- 擴充封閉證據（§7.2）：`...\score\plugins\{dummy,cus_1}\`（全 `.pyd`）、`...\score\plugins\*\__init__.py`(0 bytes)、`...\score\__init__.py`(僅 re-export `init_trainer`/`init_predictor`)、`...\workflows\entities\block_map.pyd`
- 使用手冊：`VISION\Yolo\AINavi使用手冊_2.0.4.18.pdf`（121 頁圖檔型；p028 模組目錄、p035 授權、p068 演算法僅 cls/det/seg、p082 僅匯入 AINavi 模型、p101-110 NaviFlow 須另授權）
