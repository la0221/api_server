---
date: 2026-08-05
type: daily_log
project: AIVision — 思潔 AINavi 逆向解剖（調查，未安裝未啟用）
tags: [AIVision, AINavi, Spingence, 逆向, 授權, PaddleOCR, 策略]
status: final
---

# Daily Log - 2026-08-05

## 1. 今日主題

對思潔提供的 **Spingence AINavi 3.0.1.x**（`D:\思潔的AINavi\download`，9 個安裝包共約 5 GB）做**靜態逆向解剖**，回答五個問題：如何使用／如何啟用／哪些跟我們高度相關／併行注意事項／逐漸取代計畫。

**全程只讀不裝**：沒有安裝、沒有執行任何 AINavi 程式、**沒有啟用任何授權**（原因見 §2.3 授權綁機）。

## 2. 進度

### 2.1 解包實證（拿到的硬證據）

- **完整明文 C 標頭**：`ainavi_inference.h` / `ainavi_workflow.h` —— 廠商主動提供給整合者的介面契約，函式表全可讀。
- **FastAPI 路由還原**（從 `routes.pyd` 字串）：`GET /`、`GET /details`、`POST /inference`、`POST /inference/bytes`。
- **啟動介面**（明文 `wrapper.py`）：`--model <資料夾> --port 5000 --protocol http|tcp --device gpu:0 --key <secret>`。
- **8 個演算法插件**：`ano_1/ano_2/cls_1/det_1s/seg_1/seg_2/ocr_2/cus_1`（+dummy），每個都是 `predictor + trainer + train.json + version.txt` 的固定形狀；底座 `spipe` 匯出 `Operation/DataContainer/Pipeline/ParallelOperation`。
- **心跳協定**：`.pyd` 內含完整 `DLLHeartbeatManager`（`EDGEHUB_DEFAULT_URL`/`HEARTBEAT_INTERVAL_SECONDS`/`API_SECRET_KEY`/`/heartbeat`）→ **只要推論被初始化就會主動註冊 EdgeHub 並持續送心跳**。
- CI 路徑洩漏原始碼樹：`C:\gitlab-runner\builds\...\spingence-ai\ainavi\...`；保護手段＝Cython 編 `.pyd`，只留 API 薄殼明文。

### 2.2 ⭐ 最有價值的發現：`ocr_2` 就是 PaddleOCR

三重證據：①`plugins/ocr_2/LICENSE` = `Copyright (c) 2016 PaddlePaddle Authors` + Apache 2.0 ②`train.json` 的 `input_shape: [1,3,48,320]` = PaddleOCR rec 標準輸入 ③`additional` 段用 `Global.use_gpu` 等 PaddleOCR 原生設定鍵，預訓練權重 `pw\ocr_2_en.bin`、`class_map {"0":"en"}`。

→ **直接撞上 2026-08-04 拍板的引擎策略（CRNN 字元式取代雙 head）**：AINavi 走的是同一條路，且是 PaddleOCR 產品化包裝。**因為 PaddleOCR 是 Apache 2.0，我們要同等能力可直接自建，不必經過 AINavi。**

### 2.3 ⚠ 授權機制（本日最該記住的事）

手冊 p.35：三種啟用擇一 —— **Cloud（order ID＋連網）／Dongle（插硬體鎖）／Device ID（40 hex＝160-bit 硬體指紋，綁死該機）**。額度 `Training license: 1`／`Inference license: 3`，到期 `9999-12-31`（永久）。

**掃遍推論端所有二進位，找不到 `GetAdaptersInfo`/`GetVolumeInformation`/`wmic` 等硬體指紋 API** → 該邏輯在 APP 端（Inno Setup 壓縮內，未解）→ **無法排除含 MAC**。

**結論：在問清楚授權型態前，不對任何機器按 `Activate license`；產線 edge 與 A1000 server 直接列為禁止安裝。**

### 2.4 順手結掉一個既有技術債

`2026-07-16_ainavi_edgehub_line.md` §4 記的「`AinaviOptions.DefaultModelPort` 預設 8009 但實際全是 8001」——真相是**模型推論埠由使用者部署當下在 UI 自選**（手冊 p.97 範例 8003，旁邊還有 `Check` 驗證可用性）。**「預設模型埠」這個概念本身就錯**，該欄位應廢除或改「上次使用值」。

同時**交叉驗證我們的 `AinaviApiClient` 是對的**：它實作的 `DELETE/POST :5001/services` 協定與手冊描述的 Deploy UI 行為完全吻合，不需重寫。

### 2.5 對照結論（哪些抄、哪些不抄）

- **該抄（純設計語意，零技術相依）**：`Model Filter` 的 **per-class 信心/面積/IOU 門檻**（正好是 CRNN「無 NG 類→needsReview」生產語意的答案形狀）；**pipeline 參數 JSON 外部化**（可消滅 CRNN「train/infer 一致性靠 import 訓練當下那份 .py」的痛點）；**Port 可用性檢查按鈕**。
- **不抄**：它的「一模型一行程一 Port」隔離（我們同行程獨立快取實例更省一到兩個數量級）。
- **我們已經超越**：**版本控管**——AINavi 的 Deploy 服務表只有 `Model Name/Algorithm/Service Type/GPU/Port/Size`，**沒有版本號、沒有 md5、沒有溯源、沒有回滾**；且 `Import model` 僅開放自家模型（封閉生態）。我們的 md5+`_publish.json`+狀態機明顯更嚴謹，**不可向它看齊**。

### 2.6 產出

`doc/ainavi逆開發策略/` 五份（全部標為**討論階段、未拍板**）：
`README.md`（索引＋三句話結論＋五問速答）／`01_解剖_套件與架構.md`（證據層）／`02_如何使用與啟用.md`／`03_與我們高度相關.md`／`04_並行注意事項.md`／`05_逐漸取代計畫.md`（六階段）。

全文採**證據等級標示**（【解包】/【手冊】/【本專案】/【推論】/【未知】），避免把推測當事實。

### 2.7 追加：授權機現場採集工具（使用者提到有機會借用已啟用的機器）

使用者問「需不需要到有授權的電腦上作業」——**需要，而且價值高**：四個未知數全部只存在於「跑起來的系統」，靜態解包已榨乾。

- **最高價值的一招**：AINavi 推論服務是 FastAPI（`routes.pyd` 有 `APIRouter`），**FastAPI 預設公開 `/openapi.json`** → 若廠商沒關掉，一個 GET 就拿到完整 API schema，等於廠商親手交出契約文件。這是到現場第一件事。
- 產出 `doc/ainavi逆開發策略/06_授權機現場採集清單.md`（優先順序／禁止事項／人工要錄的兩件事）＋ **`collect_ainavi.ps1` 唯讀採集腳本**（只發 GET、只讀檔，絕不 DELETE/POST，不碰授權、不裝不刪）。
- 採集 9 類：API 探測（含 openapi.json）、行程命令列（看 inference server 實際啟動參數）、監聽埠對應行程、服務/排程/自啟、GPU+CUDA+PATH（驗證 04 文件的版本衝突風險）、安裝樹清單＋雜湊（**這能看到 Inno 壓縮內容的真面目**）、小型文字設定檔、登錄檔、防火牆。
- **腳本已實測**：本機（未裝 AINavi）跑完 35 秒 exit 0，找不到目標時全部優雅降級；防火牆那步需系管權限、失敗會記一行後繼續。已修兩個會在現場咬人的問題：`$pid` 是 PowerShell 保留變數（改 `$owningPid`）、埠探測改兩段式（先判 HTTP 活埠再深探，避免最壞 15 分鐘）；檔案存 UTF-8 **with BOM**（PS 5.1 無 BOM 會把中文註解讀成亂碼→語法錯誤）。測試產物已清除。
- **主動劃掉一個題目**：原列為未知的「Device ID 指紋演算法」建議**不要挖**——我們真正要的是風險（換硬體會不會失效、額度能否回收），**直接問原廠更準確**；去逆推指紋形同做繞過授權的前置作業，越線且非所需。已寫進 06 文件 §5。

## 3. 待辦 / 未決

- **待使用者向思潔/原廠問三題**（擋住取代計畫階段 1）：①授權型態？有 dongle 嗎？②`Inference license: 3` 計數單位？重灌能否釋放回收？③能否只給訓練/驗證授權或用 Free-Trial？
- **階段 1（拋棄式機器裝一套只為錄 API 契約）待拍板**：目標是解掉 4 個未知——`POST /inference` 的 request/response schema、`Export Model` 的檔案格式、EdgeHub 5001 完整端點、是否註冊 Windows 服務。**安裝與 UI 操作依既有共識由使用者親手執行**，agent 負責出可照做清單與事後整理契約文件。
- 階段 3 三項（per-class 門檻／前處理 JSON 化／埠檢查鈕）**是否算主項 1 延伸還是支線，待拍板**。
- 🔔 沿用提醒：**CRNN 策略文件使用者尚未給**（08-04 明說會整理後補，要求主動提醒）。
- 沿用：M83 整夾、R3/R4、人工 R1/R2、P-B/P-C、TimeBudgetMs 矛盾、安全地基、pairs 的 A1/a100 盤點、vtest-0731 待刪。

## 4. 一句話總結

AINavi 拆完最大的收穫不是「它有什麼我們沒有」，而是**確認了我們在版本治理上早就贏它、在 OCR 路線上跟它同向**——真正要小心的只有那組 160-bit 的授權指紋。
