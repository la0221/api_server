---
date: 2026-07-12
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 本地把 API server 建起來（localhost + port）規劃／啟動指南
status: proposal（規劃，未實作）
tags: [API, ASP.NET Core, Kestrel, localhost, port, Swagger, 啟動指南]
---

# 規劃：在本地把 API server 建起來（localhost + port）

> 目標：像網頁後端那樣，本機啟動一個 server，用 `http://localhost:<port>` 存取；先看到骨架、再談中央推論。
> 本文件只規劃＋寫步驟，尚未動手改 code。

---

## 0. 一句話

`AIVision.Api` 就是一個 ASP.NET Core（Kestrel）web server，本來就能用 `dotnet run` 或執行 exe 在 `localhost:5030` 跑；**但它目前一啟動就崩**（DI 缺辨識器註冊），所以要先補一個註冊才跑得起來。補法有兩條路（Path A 快、Path B 正解）。

---

## 1. 「API server 用 localhost + port」是什麼意思（概念）

- **ASP.NET Core 內建 Kestrel web server**：`app.Run()` 一跑，程式就變成一個常駐的 HTTP 伺服器，監聽某個 **port**（如 5030）。
- **localhost**：`127.0.0.1`，本機自己。`http://localhost:5030` = 連到「這台機器上、監聽 5030 埠的那個程式」。跟你想的網頁一樣，只是還沒對外、只有本機看得到。
- **port 從哪來**：
  - 開發時由 `Properties/launchSettings.json` 的 profile 決定（本專案 http=**5030**、https=**7185**）。
  - 或用環境變數 `ASPNETCORE_URLS=http://localhost:5030` 覆蓋。
- **Swagger**：`Swashbuckle` 產生的互動式 API 文件頁（`/swagger`），可在瀏覽器直接點 endpoint 測試。本專案設定成**只有 Development 環境才開**（`Program.cs:50`）。
- **環境（Environment）**：`ASPNETCORE_ENVIRONMENT=Development / Production`。Development 會開 Swagger、且**啟動時嚴格驗證 DI**（見 §2 崩潰主因）。

---

## 2. ⚠️ 現況問題：一啟動就崩（要先解）

實測用 Development 啟動，直接拋例外：
```
Unable to resolve service for type 'IMoldCodeRecognizerPort'      → VerifyMoldCodeCycleCommandHandler
Unable to resolve service for type 'IMoldCodePairRecognizerPort'  → VerifyMoldCodePairCycleCommandHandler
```

- **原因**：`Program.cs:14` 用 MediatR 掃**整個 Application 組件**，自動註冊所有 handler——包含後來加進 Application 的單 head／雙 head 模號辨識 handler。但這兩顆依賴 `IMoldCodeRecognizerPort` / `IMoldCodePairRecognizerPort`，而 **API 的 DI 從沒註冊過這些辨識器**（只在 WPF `App.xaml.cs` 有）。
- **為何 Development 才爆**：Development 會 `ValidateOnBuild`（啟動即檢查每個服務能否建構）→ 抓到缺依賴就崩。Production 不做這檢查 → 會「先跑起來」，但一旦有人呼叫 `Inspection/cycle` 仍會在執行期爆。
- **本質**：雙 head handler 當初加進 Application 時，沒回頭更新 API → API 一直是壞的（沒人跑過它才沒發現）。

**結論：不管走哪條路，都得先讓 API 能解析這兩顆 handler 的依賴。**

---

## 3. 建置與啟動（步驟，補完 §2 後即適用）

### 建置
```
cd "d:\新增資料夾\VISION\AIVision\AIVision"
dotnet build "AIVision.Api\AIVision.Api.csproj" -c Debug
```

### 啟動（三種等價方式，擇一）
1. **dotnet run（開發最方便，會自動套 launchSettings 的 http profile）**
   ```
   dotnet run --project "AIVision.Api\AIVision.Api.csproj" --launch-profile http
   ```
   → 監聽 `http://localhost:5030`，環境=Development，自動開瀏覽器到 `/swagger`。
2. **直接跑 exe + 環境變數（部署較接近的方式）**
   ```
   set ASPNETCORE_ENVIRONMENT=Development
   set ASPNETCORE_URLS=http://localhost:5030
   AIVision.Api\bin\Debug\net8.0-windows\AIVision.Api.exe
   ```
3. **換 port**：把上面 5030 改成想要的埠（如 8080）即可。

### 驗證有沒有起來
- 瀏覽器開 `http://localhost:5030/swagger` → 看到 API 文件頁＝成功。
- 或指令 `curl http://localhost:5030/swagger/v1/swagger.json` → 回 JSON。

### 關掉
- 前景執行：`Ctrl+C`。
- 背景：`taskkill /F /IM AIVision.Api.exe`。

---

## 4. Path A：先讓它「能跑起來」（快、拋棄式）

**目的**：今天就看到 `localhost:5030/swagger` 活著、能點 4 個既有 endpoint。

步驟（規劃，未做）：
1. 在 `AIVision.Api/Program.cs` 的 DI 補上**最小 stub 辨識器**：
   - 註冊一個 `IMoldCodeRecognizerPort` 的假實作（回固定值即可）。
   - 註冊一個 `IMoldCodePairRecognizerPort` 的假實作。
   - 目的只是讓 MediatR 那兩顆 handler 能被建構、通過 Development 的 DI 驗證。
2. 啟動（§3）。
3. 打開 Swagger，確認 4 個 endpoint 都在：`Inspection/cycle`、`ainavi/open-model`、`ainavi/predict`、`ainavi/logs`。

- **優點**：改動小、隔離在 API 專案、可回退；不碰 WPF/產線。
- **限制**：stub 不會真的辨識，只是讓 server 站起來看骨架。

---

## 5. Path B：直接往「中央推論」建（真方向 = 規劃書 §2.5 的 P1）

步驟（規劃，未做）：
1. `AIVision.Api.csproj` 加參考 `AIVision.MoldCode.Onnx`。
2. 在 `Program.cs` 註冊**真的雙 head 辨識器**（`SwitchableTwoHeadRecognizer` → `IMoldCodePairRecognizerPort`），前處理走「相機 ROI」設定（server 收全幅圖時）或依契約決定。
3. 新增 `POST /api/infer/pair`：收圖 → 補**真正的影像解碼**（現況 `ainavi/predict` 的 `ImageData` 寬高寫死 0，見 `AinaviController.cs:123`，自己算 ONNX 前一定要修）→ 跑雙 head → 回 `{mohao, xuehao, confMohao, confXuehao}`。
4. （GPU）把 OnnxRuntime 換 GPU 版加速。
5. 啟動、用 Swagger 或 curl 丟一張已知圖驗讀值。

- **優點**：這才是你要的中央推論；一步到位。
- **成本**：較大；且要先想好 §7 的決策（節拍/GPU/前處理位置/降級）。

---

## 6. 兩條路的共同前提（不管哪條都要做）
- 解決 §2 的 DI 缺註冊（Path A 用 stub、Path B 用真辨識器）。
- 決定 API 要不要也註冊 SQLite（現況 In-Memory；只看骨架可不動）。
- Development 開 Swagger；正式部署另設環境與埠。

---

## 7. 建議順序
1. **先 Path A** → 看到 server 在 localhost:port 活著、理解啟動/Swagger/環境/埠這套（本指南 §1、§3）。
2. 補齊規劃書（`2026-07-12_api_server_deployment.md`）§10 的決策（節拍、GPU、前處理位置、降級策略）。
3. 再 **Path B** 正式建中央推論端點。

---

## 8. 檔案地標
- 啟動進入點：`AIVision.Api\Program.cs`（DI 在此補）
- 埠/環境設定：`AIVision.Api\Properties\launchSettings.json`（http=5030 / https=7185）
- 既有 endpoint：`AIVision.Api\Controllers\{InspectionController,AinaviController}.cs`
- 設定：`AIVision.Api\appsettings.json`（EdgeHub、明文金鑰 demo-secret 待處理）
- 真辨識器（Path B 要接）：`AIVision.MoldCode.Onnx\SwitchableTwoHeadRecognizer.cs`
- 部署大方向：`.ai\designs\2026-07-12_api_server_deployment.md`
