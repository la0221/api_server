---
date: 2026-08-10
type: daily_log
project: AIVision — AINavi 遠端採集（暫借授權機，唯讀）
tags: [AIVision, AINavi, Spingence, API契約, 授權, PaddleOCR, EdgeHub]
status: final
---

# Daily Log - 2026-08-10

## 1. 今日主題

思潔暫借一台已安裝 AINavi 的機器（`192.168.0.222`，直連 TCP/IP）。**遠端唯讀採集**到 EdgeHub 5001 的完整 API 契約。全程只發 HTTP GET：未啟用授權、未呼叫任何 DELETE/POST、未關服務、未下載模型權重。

## 2. 過程

- **連通性排查**：先掃兩網段（192.168.1.x/0.x）只有路由器回應、無掛載磁碟、無直連鄰居 → 判斷碰不到；使用者回報機器設在 `192.168.0.222`。針對它直測：ping 通、**5001(EdgeHub)/445/139 開**、鄰居 Reachable。
- **關鍵突破**：EdgeHub 是 **FastAPI + uvicorn**（`/` 回 404 但 `server: uvicorn`）→ `GET /openapi.json` **完整吐出 46 端點契約**（先前 PowerShell Invoke-WebRequest 回 0 是工具問題，改用 curl 即通）。
- 系統性抓所有唯讀 GET：info/health/device/license/license.device/service/default_model/models(各 category)/port 檢查範例。
- `/models` 回 40MB（含客戶模型預覽 base64）→ **抽出去 base64 的精簡中繼資料後刪除原始 40MB 檔**（資料最小化+法務界線）。
- 證據存進專案 `doc/ainavi逆開發策略/遠端採集_20260810_edgehub5001/`，寫成 `07_遠端採集實錄.md`。

## 3. 重大發現（一手證據）

- **這台機器目前「未啟用」**：`GET /api/license` 八種 SKU 全 0、`success:false` → 我方讀契約零風險、不燒額度；也揭露完整 SKU 清單（Trainer/Inference/Spinmind/Trainer-Basic/Inference-Basic/**Workflow**/**OCR**/**Defect-Generator**，OCR 與 Defect 都獨立計費）。
- **這台就是 A1000 機**：`GET /device` → `NVIDIA RTX A1000 / 8GB`（＝我方多站並行拍板要用的卡）。
- **Device ID 唯讀可取**：`GET /api/license/device` → `A8B2-D1B8-EEC9-DCE2-C801-7308-5AA1-F729-5025-E92E`（坐實 40-hex 指紋）。
- **授權機制全貌**：`.lic`=Device 綁機／**`.v2c`=Sentinel HASP dongle**／order id=Cloud，且 **Cloud 有 `DELETE /api/license/cloud/{key}` 可自助反啟用**；Device 無對應反啟用端點 → 綁機最難解，建議優先 dongle/cloud。
- **ocr_2=PaddleOCR 三度坐實**：機上 OCR 模型 `ocr_2` 尺寸 320×48×3，且有**中文字集**版（`ainavi_ocr_ch_2`）。
- **Export Model 格式=ZIP**（`GET /zip_model?model_name=` 回 zip；我方未下載，守界線）。
- **埠檢查契約**：`GET /api/port/{port}` → `{"message":"Port is available"}`。
- ⚠ **它機器上有隱形眼鏡模型**：`HJ_Cavity_260724_40pcs`（cls_1，none/onlylens）、`HJ_Cavity_260803`（cls_1 四態 n_b/o_b × none/onlylens）——**與我方公母模脫膜/隱形眼鏡同領域**，但走「640 整圖分類」路線（我方走雙head/CRNN 字元式 + 公模四態專用定心，技術路線不同）。日期 260724/260803＝思潔正在進行的活。**別人的客戶模型，我方不下載不取用，只記錄事實供對照。**

## 4. 對既有判斷的影響

- **主項 1**：AINavi 模型倉庫有 category/plugin 版本/class_map/train_overview，**但仍無 md5/溯源/版本狀態機/回滾** → [03 §7] 結論不變（我方治理較嚴謹）。可借鏡其 `GET /model` 的「模型自述」欄位補進我方 `_publish.json`。
- **[01 §7] 四個未知**：API 契約 ✓、Export 格式 ✓(zip)、EdgeHub 端點 ✓(46 個)、是否註冊服務→DLL 推論確實走 EdgeHub 註冊+心跳 ✓。剩 `POST /inference`（推論服務層）需有授權起服務後才有，但已知端點名。
- **[02/04] 授權**：三題可更精準地問（見下）。

## 5. 待辦 / 未決

- **請使用者問思潔三題（升級版）**：①給哪種授權？→ 優先 **Cloud（可 DELETE 回收）或 Dongle（.v2c 插拔）**，避免 Device 綁機（無自助反啟用）。②授權含不含 **`AINavi-OCR`** SKU？（沒有就起不了 ocr_2、對照試做不成）③這台 A1000 可借到什麼程度？→ 最有價值＝在上面量 **A1000 單張/2-3 站並發推論延遲**（我方 P-B 前置），但須不污染環境、用完復原。
- **未做（守界線）**：`GET /zip_model` 實際下載（會取客戶權重）；SMB 445 檔案分享（需認證、非必要）。若思潔願提供一顆「範例模型」匯出檔，可確認 zip 內部（ONNX/pt？有無 _publish 對等物）。
- 🔔 沿用：CRNN 策略文件使用者仍未給（要主動提醒）。
- 沿用：M83 整夾、R3/R4、人工 R1/R2、P-B/P-C、TimeBudgetMs 矛盾、安全地基、pairs 的 A1/a100 盤點、vtest-0731 待刪。

## 5.5 追加：AINavi APP 三層架構（同日下午，暫借機用完前）

使用者在暫借機看到「AINavi APP」，要求納入計畫。遠端唯讀抓前端首頁＋JS bundle 靜態分析（未點 UI、未寫入）：

- **APP 是三層式 Web，不是桌面程式**：① 前端 `:8000` React/create-react-app（TornadoServer，純靜態 SPA）② 資料/訓練後端 `:5002` **Flask（Werkzeug, Python 3.10.18）**＝Project/Dataset/Label/Train/Validate/Workflow ③ 部署/推論/授權後端 `:5001` FastAPI EdgeHub（[07] 46 端點）。
- **標註底層是 Label Studio**（前端有 `/labelstud…` 路徑）→ 我方若需標註能力可直接用開源，不自寫。
- **職責切法**：Flask 扛資料 CRUD、FastAPI 扛即時推論，兩種框架各承載一種負載；Python 環境也分兩套（推論 3.9／資料工具鏈 3.10）。
- **判斷（寫入 08 文件）**：AINavi APP＝全生命週期多人 Web 平台；我方＝產線推論+版控專用 WPF 工具，**定位不同**。做 AINavi 式大 Web 平台對不上三大主項＝發散 → **建議不新增 ROADMAP 主項，只記為架構參照**（可低成本借鏡兩點：資料/推論分層、標註用 Label Studio）。**真正的岔路問題留給使用者拍板**：要維持 WPF 產線工具定位，還是轉型一站式 AI 平台？拍板前不動 ROADMAP。
- 產出 `08_ainavi_app_三層架構.md`＋證據（app8000_index.html、app5002 端點清單；6MB bundle 未入庫）。

## 5.6 追加：APP 三層修正 + 觀察紀錄文件（暫借機用完前）

使用者指示「都先探探看，然後寫文件描述在那台看到啥；要不要做那麼複雜之後再議」。續探（全唯讀）：

- **修正 08 的一個判斷**：主資料 API 其實在 **8000（Tornado 同時服務 React 靜態＋ `/api/v2/*` REST）**，實測 `GET :8000/api/v2/projects/` 回真 JSON `{"projects":[]}`；**5002 是 auto-label 輔助後端**（前端 config `autoLabelPort:5002`、`REACT_APP_AUTO_LABEL_API_PORT`），非主資料層。三層＝8000 前端+資料/5002 auto-label/5001 EdgeHub。
- **APP 工作區是空的**：projects 回 `[]`，但 EdgeHub 有 5 顆模型 → 研判模型直接部署在 EdgeHub 或從他機發布，這台像展示/部署機。
- 主資料 API 面（bundle 擷取）：`/api/v2/` 下 projects/datasets(create-from-crops)/images/label/validation-sets/migration。
- **寫成觀察型文件 `09_那台電腦上看到什麼.md`**（純描述、不談策略，照使用者要求）：涵蓋機器規格(A1000)、三層架構、授權未啟用、5 顆模型(3 OCR + 2 隱形眼鏡分類)、界線聲明。

## 6. 一句話總結

一個 `GET /openapi.json` 換來 46 端點的完整契約＋授權機制全貌＋確認同領域競品在用整圖分類——這台借來的機器還剛好是張 A1000，未啟用狀態讓我們看得毫無顧慮。
