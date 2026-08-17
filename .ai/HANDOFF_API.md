# AIVision — API Server 交接文件（HANDOFF_API）

> 最後更新：2026-07-31（全面重寫；前版停在 07-14 已過時）
> 用途：下一個 session 無縫接手「API server / 中央推論」這條線。**先讀本檔 → 再看 `ROADMAP.md`（根目錄，主線錨）→ 細節走 §8 文件地圖**。
> 主專案交接：`.ai/HANDOFF.md`（WPF 線，較舊）；每日進度：`.ai/status.json` + `records/`。

---

## 0. 一分鐘摘要（2026-07-31 現況）

- **中央推論已全通**：`POST /api/infer/pair`（PNG/raw、fail-closed、`stationId` 回聲）+ `GET /api/infer/health`。M101 180 張讀值 **180/180**；Release+Passes=2 現行約 **320-385ms/張**；**Passes=1 大樣本 180/180 @p50 136ms（07-27）**，過 M83 整夾驗證就可設預設。
- **Edge 整合階段 0-2 完成**：`RemotePairRecognizer`（raw 免編碼）、雙head頁「測試中央推論」驗收按鈕、批量頁「來源=本機/中央伺服器」隔離試模、「API 伺服器設定」視窗（清單自建+`%LocalAppData%` 持久化）。生產熱迴圈**未動**（`Enabled=false`）。
- **EdgeSimulator**（獨立零依賴 WPF，`AIVision.EdgeSimulator\`）已建：純 HTTP 接 server＝契約試金石；單張/資料夾批量/健檢/動作示意。
- **多站×多模型架構已拍板**（白板）：路線1..n 上位機 → HTTP 一次來回（**不輪詢**）→ server 多 task（ocr_pair✅／gongmu 使用者訓練中／defect 未開工）→ 回 JSON，**決策永遠在 edge（fail-closed）**。GPU=**A1000**（未到，3050 代量）。
- **⚠ 並發串行已實證**：3 站同時 → **356/619/864ms 排隊階梯**（server 推論鎖）→ 多站前必做 P-B(GPU)+P-C(解串行)。
- **發布**：本地鏈已實測全綠（`publish_pair_model.ps1`→md5→harness gate；vtest-0731 四道驗證過＋UI 消費收尾完成，**資料夾待使用者手動刪**）。
- **發布 server 段 R1/R2/R5 已通（07-31 下午，使用者驗收過）＋ 模型倉庫 task 化 + UI 發布頁（07-31 傍晚）**：模型按**用途**分家（`ocr_pair`=pairs 雙檔／`gongmu`／`defect`，appsettings ModelRegistry:Tasks）；`GET /api/models`（用途總覽）、`GET/POST /api/models/{task}`（列版本／**上架**=multipart+md5+原子落地+**409 版本不可變**）、`GET /{task}/{version}/download?file=`（X-Model-Md5）、`POST /api/infer/pair` 指定 `modelVersion`＝**隔離試模**（僅 ocr_pair；按版本獨立快取不動 baseline；未知 404）。edge 端＝**「模型發布」視窗（工程師以上）**選用途→選檔→版本號→HTTP 上傳（跨機可發布，取代 ps1 腳本）＋批量頁指定版本整批試＋設定視窗用途下拉「取得清單→下載到本地」（**md5 複驗不符即拒收**）。客戶端職責分離：`ModelHubClient`（模型生命週期）vs `RemotePairRecognizer`（推論）。剩 R3（金樣本 gate）/R4（promote/stable）/定時自動拉；gongmu/defect 推論端點待模型。⚠ 發布 API 無認證，角色把關只在 UI 層。

- **CRNN 引擎已接入（07-31 A+C；08-04 使用者發布 b3 並驗收；08-06 升級多版本）**：倉庫 task `ocr_crnn`（兩顆 .pt）；`POST /api/infer/ocr_crnn` 經 python sidecar（跑驗證區 OCR_demo `--serve`，`-B` 禁寫 pyc 守四區唯讀）。**多版本行程池（08-06）**：請求帶 `modelVersion` 指定登錄庫任一版本（免改設定免重啟）、未帶=appsettings `CrnnSidecar:DefaultVersion`（現=b3）、MaxProcesses=2+LRU；⚠ 舊設定鍵 DetectorPath/NonarPath/VersionLabel **已廢**。無 NG 類（品質旗標=needsReview，門檻可隨版本版控見下）、v1 僅 png。CRNN 專屬測試頁=面板→模型與測試→CRNN 測試。調查+路線：`2026-07-31_crnn_engine_intake.md`；策略正典=開發區 `CRNN_策略總覽.md`（⚠ 正典權重 v4，登錄庫現只有 b3=v3，待使用者發布）。
- **AINavi 借鏡五項全完成（08-06，經對抗性複查，驗證清單=`doc/2026-08-06_借鏡五項_驗證清單.md`）**：①CRNN 多版本熱切換（同上）②judge 門檻隨版本進 `_publish.json`（發布頁選填；CRNN 按版本門檻算 needsReview+回聲）③前處理參數隨版本進 `_publish.json`（ocr_pair 指定版本推論採用；鍵名錯發布即 400；差異化參數實測證明生效）④伺服器設定視窗「測連接埠」（TCP 探測）⑤PaddleOCR 開源對照試：模號 97.08%/穴號 39.30%（1061 對 zero-shot）→ CRNN 針對性價值實證，報告=`experiments/paddleocr_compare/REPORT_三方對照.md`。
- **四區規則（08-04，鐵律）**：開發區 Content_lens_OCR／驗證區 OCR_demo／穩定區 模號檢驗＝**唯讀**（使用者要求改也要阻止）；本專案=釋出區。sidecar 借驗證區程式碼屬臨時方案（可攜包複製進釋出區待拍板）。

## 1. 怎麼跑（環境速查）

```powershell
# API server（量測一律 Release；Debug 慢一倍不可信）
cd "d:\新增資料夾\VISION\AIVision\AIVision"
dotnet run --project "AIVision.Api\AIVision.Api.csproj" -c Release
# 驗活：http://localhost:5030/api/infer/health → "status":"ready"

# 主 App（必以 exe 目錄為工作目錄，否則登入清單空）
$dir="...\AIVision.Presentation.Wpf\bin\Debug\net8.0-windows\win-x64"; Start-Process "$dir\AIVision.exe" -WorkingDirectory $dir
# 帳號 vendor/admin888、eng1/1234、op1/1234

# EdgeSimulator（獨立）
...\AIVision.EdgeSimulator\bin\Debug\net8.0-windows\EdgeSimulator.exe
```
- server 模型：appsettings `MoldCodeWarpPolar` → `D:\AIVisionModels\v671\`（版本標籤顯示 "baseline"）、**Passes=2（保守現行）**、`Roi*=0`（收已裁圖，**勿抄 WPF 的相機 ROI**）。
- App 端 `InferenceServer`：`TimeoutMs=2000`（**試模用**；生產實時要另設 <節拍）；`Enabled=false`；KnownServers/自建清單存 `%LocalAppData%\AIVision\inference_servers.json`（**刻意不寫 bin**）。
- 測資：`...\2026_06_05_yolo模號穴號\2026-06-05\M101`（18穴）、`D:\M83_收圖2\M83_收圖1\M83`（12穴）；批量頁有下拉（appsettings `TestImageFolders`）。
- ⚠ build 前先關 App/server（檔案鎖）；DLL 改完 App 要重啟。

## 2. 已拍板的關鍵決策（勿重新發明）

| 決策 | 內容 |
|---|---|
| 節拍 | **<400ms** → CPU Passes=1 可行（136-191ms）；Passes=2 現行為保守預設 |
| 協定 | HTTP 一次 request/response、**不輪詢**；MQTT 只留控制面、gRPC 條件觸發 |
| 職責 | server 只回 JSON 觀測（讀值+信心+版本+站點回聲）；**三態決策/氣吹永遠在 edge**；逾時→fail-closed/本機後援不停線 |
| 多模型 | task 化：ocr_pair→gongmu→defect；做成 AINAVI 類似中樞；AINAVI 盒子本身不投入（機器不在） |
| GPU | A1000（未到位，先 RTX 3050 代量）；**已升格為多站並行前提** |
| 傳圖 | 無損（PNG/raw）；禁 JPEG（415 擋） |

## 3. 進行中 / 待辦（依優先序）

1. ~~使用者收尾~~ ✅（07-31 下午）：雙head頁載 `vtest-0731` 小批量 OK，「發布→消費」閉環；**剩使用者手動刪 `D:\AIVisionModels\pairs\vtest-0731`**（agent 刪除被權限擋）。
2. **M83 整夾一石二鳥**：跨模號驗證 + Passes=1 超大樣本 → 過了把 server 預設改 Passes=1 + ROADMAP「線上×離線」轉 ✅。
3. **人工 R1/R2**（面板收斂 Phase 1）：照 `doc/2026-07-24_人工測試執行單.md`（M1-M8+M3b）；兩輪全綠才可 Phase 2 刪檔（**前置：git init——專案至今無版控！**）。
4. ~~發布 server 段 R1/R2/R5~~ ✅（07-31 下午，端到端 agent 實測全綠）：**待使用者 UI 點測兩處**——批量頁「查伺服器版本→指定版本」整批試模、設定視窗「取得模型清單→下載到本地」。剩 **R3**（金樣本+零退步 gate 自動化；金樣本集定版是前置）→ **R4**（promote/demote+stable 標記）→ `POST /api/models` 上架 → edge 定時自動拉 stable。程式地標：`AIVision.Api\Services\ModelRegistryService.cs`、`Controllers\ModelsController.cs`、`RemotePairRecognizer.{ListModelsAsync,DownloadVersionAsync}`。
5. **P-B GPU 化**（3050 代量；⚠ `MoldCode.Onnx` 被 edge/server 共用，CUDA 依賴要套件切分）→ **P-C 解串行**（ORT Run 官方執行緒安全；鎖只該保護 swap）→ 並發壓測 p95<節拍。
6. **階段 3/4（線上×實時）**：手動來源開關 + Shell SRV 燈（⚠ 先解狀態模型：init 一次性 vs 連線持續性，建議週期健檢+專屬 event）→ 自動降級。
7. 待拍板：軟體版本控管範圍、AINAVI 檔案刪否、v6 正版來源（v671 vs pairs）、gongmu 接入（使用者訓練中，接入規格待寫）。

## 4. 未解風險（動實時前必看）

- ⚠ **edge `TimeBudgetMs=120` vs 單幀 136-385ms 矛盾**：多幀投票恐實際只跑 1 幀——接實時前必釐清。
- ⚠ 多站串行鎖（§0 實證）；節拍餘裕薄（wall p90 289ms localhost，真網路+TLS 再加）。
- ⚠ 安全地基：http 明文、demo-secret、In-Memory——上線前必補（部署書 §7）。
- ⚠ 已知 legacy 壞點（07-27 測試 Fail2，低優先）：`ainavi/predict` 不可達 hang>15s、`Inspection/cycle` 500+Dev 洩 stack trace。

## 5. 重要教訓（避免重踩）

- **效能量測一律 Release**（Debug 慢 2 倍，曾誤判「CPU 出局」）。
- **逾時要對齊實際部署組態**，不是最佳量測值（350ms vs Passes=2 必逾時案例）。
- **前處理參數分兩類**：對齊訓練的（必抄）vs 對齊取像來源的（ROI，**不可抄**）。
- **>= 型長度校驗=診斷黑洞**（改精確比對）；**bin 目錄寫檔會被 rebuild 蓋掉**（models.online.json 之坑→一律 %LocalAppData%）。
- 收斂 UI 前**先探查現有慣例**（燈號/Converter/事件機制）。

## 6. 程式地標（本線新增/核心）

- Server：`AIVision.Api\Controllers\InferController.cs`（pair+health+stationId 回聲）、`Program.cs`（雙辨識器 DI）
- Edge 適配：`AIVision.Infrastructure\MoldCode\{RemotePairRecognizer,InferenceServerOptions}.cs`
- UI：`MoldCodePairBatchView(+VM)`（驗收鈕+來源選擇+資料夾下拉）、`ServerSettingsView(+VM)`+`Services\InferenceServerListStore.cs`、`ShellView.xaml`（收斂後選單；回退備份 `doc\test\phase1_backup\`）
- 模擬器：`AIVision.EdgeSimulator\`（零依賴）
- 發布：`D:\AIVisionModels\publish_pair_model.ps1`、`pairs\<版>\_publish.json`；harness：`AIVision.MoldCode.Harness...exe paircycle <mohao> <xuehao> <資料集根(根\模號\穴號)>`

## 7. 一句話總結

中央推論線「本地×離線／線上×離線」已全通且工具齊備（驗收鈕/批量試模/EdgeSimulator/發布鏈），拍板了多站多模型架構與信任鏈設計；下一步依序：M83 驗證收尾 → 發布 R1-R5 → GPU+解串行 → 實時接線；動實時前先解 TimeBudgetMs 矛盾。

## 8. 文件地圖

| 主題 | 檔案 |
|---|---|
| **主線錨（先讀）** | `ROADMAP.md`（根目錄） |
| 操作/排錯手冊 | `使用流程_中央推論.md`（根目錄） |
| 多站×多模型架構（最新定案） | `designs/2026-07-24_multi_model_server_architecture.md` |
| 發布全鏈路+信任鏈 | `designs/2026-07-31_model_release_and_trust.md` |
| 本地發布路線（已實測） | `designs/2026-07-24_model_publish_route.md` |
| Edge↔Server 整合階段 0-4 | `designs/2026-07-15_edge_server_integration.md` |
| 契約 / 協定 / 部署 | `designs/2026-07-14_api_infer_pair_contract.md`、`2026-07-14_api_transport_protocol.md`、`2026-07-12_api_server_deployment.md` |
| 面板收斂三件套+執行單 | `doc/2026-07-24_面板*.md`、`doc/2026-07-24_人工測試執行單.md` |
| API 測試計畫/快照（07-27） | `doc/api_test/` |
| AINAVI 盤點（非主項） | `designs/2026-07-16_ainavi_edgehub_line.md` |
| 逐日細節 | `records/2026-07/{14,15,16,24,27,31}/` |
