---
date: 2026-07-14
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: POST /api/infer/pair — 雙 head 中央推論 API 契約（階段 1 / HTTP）
status: spec（契約草案，未實作）
tags: [API, 契約, contract, infer, 雙head, HTTP, multipart, PairObservation]
---

# API 契約：`POST /api/infer/pair`（雙 head 中央推論）

> 對應 `2026-07-14_api_transport_protocol.md` 的**階段 1（HTTP）**、部署書 `2026-07-12_api_server_deployment.md` §2.5 的 **P1**。
> 職責：edge 送**前處理後的圖** → server 跑雙 head warpPolar ONNX → 回**讀值+信心**。**決策（三態/氣吹）不在此，永遠留 edge。**
> 本契約對齊真實 domain 型別：請求 → `ImageData`（`AIVision.Domain.Shared`）、回應 → `PairObservation`（`AIVision.Domain.MoldCode`）。

---

## 0. 一句話

`POST /api/infer/pair`，multipart 上傳一張前處理圖（含尺寸中繼），回 JSON `{ objectPresent, mohao, confMohao, xuehao, confXuehao, hasReading, failureReason }`。**fail-closed**：辨識失敗回結構化失敗，不回「看似合法」的碼。

---

## 1. Endpoint

| 項 | 值 |
|---|---|
| 方法 / 路由 | `POST /api/infer/pair` |
| Content-Type（請求） | `multipart/form-data` |
| Content-Type（回應） | `application/json` |
| 認證 | 階段 1 沿用現況（暫無/demo）；正式走 API Key/mTLS（見部署書 §7） |
| 冪等 | 是（同圖同模型 → 同結果）；可安全重試 |

---

## 2. 請求（multipart/form-data）

| part 名 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `image` | file（binary） | ✅ | 前處理後的圖。見 §2.1 兩種格式 |
| `format` | text | ✅ | `png`｜`raw`。決定 server 怎麼還原 `ImageData` |
| `width` | text(int) | raw 必填 | 影像寬（像素）。**raw 一定要帶**（修現況寫死 0 的 bug） |
| `height` | text(int) | raw 必填 | 影像高（像素） |
| `pixelFormat` | text | raw 必填 | `Mono8`｜`Bgr24`（對齊 `ImageData.PixelFormat`） |
| `stride` | text(int) | 選填 | 每行位元組數；省略 = 0 = 預設計算（對齊 `ImageData.Stride`） |
| `modelVersion` | text | 選填 | 指定模型版本；省略 = server 現用 stable。回應會回填實際用的版本 |
| `stationId` | text | 選填 | **站點識別**（多站架構「站點通知」；2026-07-31 P-A 第一片）。自由字串（如 `ST-01`），server 原樣回聲於回應 `stationId` 欄位 |

### 2.1 兩種影像格式

- **`png`（推薦・預設）**：`image` part 放 **PNG bytes**。自帶寬高、**無損**（保準確度鐵律）、順帶壓縮省頻寬。server 端解碼即得 `ImageData`，`width/height/pixelFormat` 可省。
- **`raw`**：`image` part 放**原始像素 buffer**（Mono8/Bgr24）。**必須**同時帶 `width/height/pixelFormat`，否則 server 無法還原 `ImageData`（這正是現況 `AinaviController.cs:123` 寫死 0 的坑）。省一次編碼，但頻寬較大。

> ⚠️ 禁止有損格式（JPEG 等）：會傷辨識準確度。要壓縮只用 PNG。

### 2.2 範例（curl，PNG）

```bash
curl -X POST http://localhost:5030/api/infer/pair \
  -F "image=@lens_preprocessed.png;type=image/png" \
  -F "format=png"
```

### 2.3 範例（curl，raw Mono8）

```bash
curl -X POST http://localhost:5030/api/infer/pair \
  -F "image=@lens.raw;type=application/octet-stream" \
  -F "format=raw" -F "width=640" -F "height=640" -F "pixelFormat=Mono8"
```

---

## 3. 回應（200 OK, application/json）

直接對映 `PairObservation`（欄位 camelCase）：

```json
{
  "objectPresent": true,
  "mohao": "M101",
  "confMohao": 0.983,
  "xuehao": "08",
  "confXuehao": 0.971,
  "hasReading": true,
  "failureReason": null,
  "modelVersion": "v6.7.1",
  "elapsedMs": 42
}
```

| 欄位 | 型別 | 說明（對映 `PairObservation`） |
|---|---|---|
| `objectPresent` | bool | 是否偵測到鏡片（Hough 命中）。false = NO OBJECT |
| `mohao` | string? | 模號 top-1（如 `M101`，可能 `NG`）；無讀值 = null |
| `confMohao` | double | 模號 top-1 信心（0..1） |
| `xuehao` | string? | 穴號 top-1（如 `08`）；無讀值 = null |
| `confXuehao` | double | 穴號 top-1 信心（0..1） |
| `hasReading` | bool | 兩軸是否都讀到碼（= `PairObservation.HasReading` 計算值） |
| `failureReason` | string? | 失敗原因（有讀值時 null） |
| `modelVersion` | string | server 實際使用的模型版本（回填，供 edge 對版） |
| `elapsedMs` | int | server 端推論耗時（不含網路），供延遲量測（見部署書 §8 P0） |

> **fail-closed 語意**：`objectPresent=false`（NoObject）或 `hasReading=false`+`failureReason`（Failed）都是**正常 200 回應**，不是 HTTP 錯誤——因為「沒讀到」是有效的觀測結果，交給 edge 的 `MoldCodePairVerifier` 判三態。**HTTP 4xx/5xx 只留給「請求壞掉/server 出錯」**（見 §4）。

---

## 4. 錯誤（ProblemDetails, application/problem+json）

沿用現況 `AddProblemDetails`。

| HTTP | 情境 |
|---|---|
| 400 | 缺 `image`／`format`；`format=raw` 卻缺 `width/height/pixelFormat`；尺寸與 buffer 長度對不上 |
| 413 | 圖過大（超過設定上限） |
| 415 | 不支援的格式（如送了 JPEG） |
| 503 | 模型未載入／server 尚未就緒 |
| 500 | 推論過程未預期例外 |

> edge 收到**逾時或 5xx/503** → 觸發「推論來源選擇器」降級用本機 ONNX（部署書 §8 P2）。**辨識失敗（NO OBJECT / Failed）不是錯誤**，不觸發降級。

---

## 5. 實作對接點（給 Path B）

1. `AIVision.Api.csproj` 加參考 `AIVision.MoldCode.Onnx`。
2. `Program.cs` 註冊真雙 head 辨識器 → `IMoldCodePairRecognizerPort`（照 WPF `App.xaml.cs:179-189` 的 `SwitchableTwoHeadRecognizer` 範式）。順帶解掉 §DI 崩潰（`HANDOFF_API.md` §3）。
3. 新增 `InferController`（或擴充 `AinaviController`）掛 `POST /api/infer/pair`：
   - 解 multipart → 依 `format` 還原 `ImageData`（**png 解碼得寬高**／**raw 用帶入的寬高**，修掉寫死 0）。
   - 呼叫 `IMoldCodePairRecognizerPort.Recognize(image)` → `PairObservation`。
   - 映射成 §3 JSON（含 `modelVersion`、`elapsedMs`）。
4. （GPU）OnnxRuntime 換 GPU 版加速（有 NVIDIA GPU 時）。
5. 用 §2.2 curl 丟已知圖驗讀值 + 量 `elapsedMs`。

---

## 6. 待拍板 / 未定
- **請求圖預設格式**：png（推薦，無損+自帶尺寸）vs raw（省一次編碼）——建議 png 起步。
- **前處理位置**：本契約假設 edge 已做 warpPolar/ROI（送小圖）；若改「送原圖、server 前處理」，`image` 改帶原幅圖、server 端加前處理（部署書 §10 決策 4）。
- **多幀**：本契約單張一次。批次（送 N 幀 server 投票）另開 `?frames=N` 或 streaming，屬效能演進（協定書階段 3）。
- **圖大小上限**：413 門檻值待定。

---

## 7. 檔案地標
- 本契約：`.ai\designs\2026-07-14_api_infer_pair_contract.md`
- 協定選型/演進：`.ai\designs\2026-07-14_api_transport_protocol.md`
- 中央推論設計：`.ai\designs\2026-07-12_api_server_deployment.md` §2.5
- 回應型別：`AIVision.Domain\MoldCode\PairObservation.cs`
- 請求影像型別：`AIVision.Domain\Shared\ImageData.cs`
- 要接的辨識器：`AIVision.MoldCode.Onnx\SwitchableTwoHeadRecognizer.cs`（port：`AIVision.Application\Ports\MoldCode\IMoldCodePairRecognizerPort.cs`）
- WPF 註冊範式：`AIVision.Presentation.Wpf\App.xaml.cs:179-189`
- 現況待修的寫死 0：`AIVision.Api\Controllers\AinaviController.cs:123`
