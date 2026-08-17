---
date: 2026-07-14
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）— API server 中央推論
tags: [ASP.NET Core, DI, MediatR, ValidateOnBuild, ONNX, 影像解碼, fail-closed]
status: draft
promote_to_pitfall: true
---

# Bug Notes - 2026-07-14

## Bug 1：API server 一啟動就崩 — MediatR 自動註冊 handler，但 API 沒註冊 handler 依賴的辨識器 port

### 1. 錯誤情境

第一次嘗試在本地把 `AIVision.Api` 用 Development 模式跑起來（`localhost:5030`），程序啟動即拋例外、server 根本沒站起來。

### 2. 錯誤現象

```
System.AggregateException: Some services are not able to be constructed
  → Unable to resolve service for type 'IMoldCodeRecognizerPort'
      while activating 'VerifyMoldCodeCycleCommandHandler'
  → Unable to resolve service for type 'IMoldCodePairRecognizerPort'
      while activating 'VerifyMoldCodePairCycleCommandHandler'
```
發生在 `WebApplicationBuilder.Build()`（`Program.cs`）。

### 3. 已嘗試但失敗的方法

（直接定位，無多餘嘗試。）關鍵觀察：build 明明 0 錯，卻在啟動崩 → 是「執行期 DI 圖」問題，不是編譯問題。

### 4. 最終原因

- `Program.cs` 用 `AddMediatR(RegisterServicesFromAssemblyContaining<StartInspectionCycleCommand>())`，會把**整個 Application 組件**的所有 `IRequestHandler` 自動註冊 —— 包含後來才加進 Application 的單/雙 head 模號核對 handler（`VerifyMoldCodeCycleCommandHandler` / `VerifyMoldCodePairCycleCommandHandler`）。
- 這兩顆 handler 依賴 `IMoldCodeRecognizerPort` / `IMoldCodePairRecognizerPort`，但這些辨識器**過去只在 WPF `App.xaml.cs` 註冊**，API 從沒註冊。
- Development 環境預設 `ValidateOnBuild = true`，啟動時就檢查每個服務能否建構 → 抓到缺依賴直接崩。（Production 不做此檢查會「先跑起來」，但一呼叫該 handler 仍在執行期爆。）
- 本質：雙 head handler 加進 Application 時，沒回頭同步更新 API 專案的 DI。

### 5. 最終解法

在 `Program.cs`（`AddFakeInfrastructure` 之後）註冊兩個**真辨識器**（同 WPF 範式）：
- `IMoldCodeRecognizerPort` → `SwitchableMoldCodeRecognizer`（+ `MoldCodeOnnxOptions`）
- `IMoldCodePairRecognizerPort` → `SwitchableTwoHeadRecognizer`（+ `MoldCodeWarpPolarOptions`）
兩者建構子皆「缺模型檔只記錄、不拋」→ 即使 appsettings 沒配 `.onnx` 路徑也能啟動，`Recognize` 回 fail-closed 觀測。**只註冊辨識 port，不註冊 UI-only 的執行期切換 port（`IMoldCodePairModelSwitch` 等 API 不需要）。**

> 選「註冊真辨識器」而非 stub：啟動需同時解析單/雙 head，直接上真辨識器最乾淨，也順帶讓 `Inspection/cycle` 與新 `infer/pair` 都能用。

### 6. 下次遇到類似問題，AI 應先檢查

- 「build 0 錯卻啟動即崩」→ 先看是不是 **DI 驗證**（Development `ValidateOnBuild`）抓到缺依賴。
- 用 **MediatR 掃組件自動註冊** 時，記得：組件裡**每一顆 handler 的建構子依賴**都必須在該 host 的 DI 補齊。跨 host（WPF vs API）共用同一個 Application 組件時，**新增 handler 要同步更新所有 host 的 DI**。
- 想「先跑起來看」可暫用 Production 環境略過驗證，但那只是延後爆點到執行期，治本仍是補註冊。

### 7. 是否應升級成避坑指南？

- [x] 已驗證　[x] 容易重複踩坑（每次在 Application 加 handler，其他 host 都可能中）　[x] 未來應排除　[x] 對開發決策有約束價值

結論：yes（「MediatR 自動註冊 + 多 host 共用組件 → 新 handler 的依賴要在每個 host 補齊」是通用陷阱）。

---

## Bug 2：既有 `ainavi/predict` 把上傳影像的寬高寫死 0 — 自算 ONNX 會壞，infer/pair 已修

### 1. 錯誤情境

要在 API 端自己跑雙 head ONNX（不再只是轉發 EdgeHub）時，需要把上傳的影像還原成 `ImageData` 餵辨識器。

### 2. 錯誤現象

既有 `AinaviController.Predict`（`AinaviController.cs:123`）建 `ImageData` 時 `Width: 0, Height: 0`（註解自承「簡化處理，實際應從圖片解析」）。之所以沒出事，是因為它只把原始 bytes 轉發給 EdgeHub 去解 —— 一旦改成本機 ONNX 前處理，寬高 0 會直接壞掉。

### 3. 最終原因

原 endpoint 只當 proxy，從沒真的解碼影像；`ImageData` 的尺寸欄位是空殼。

### 4. 最終解法

新 `InferController`（`POST /api/infer/pair`）自己負責解碼：
- `format=png` → `MoldCodeImageLoader.LoadFromBytes()`（`Cv2.ImDecode` 路徑，前處理與訓練/WPF 對齊）；並用 **PNG 魔術位元組**把關、**禁 JPEG**（有損會傷辨識準確度）。
- `format=raw` → 用帶入的 `width/height/pixelFormat(Mono8/Bgr24)/stride` 組 `ImageData`，並校驗 buffer 長度 ≥ `stride×height`，不足回 400。
- 解碼失敗（`image.Bytes.Length == 0`）回 400。

### 5. 下次遇到類似問題，AI 應先檢查

- 沿用舊 proxy 端點改成「本機真算」時，先查它有沒有**真的解析影像中繼**（尺寸/像素格式），proxy 常把這些留空。
- 傳圖務必**無損**（PNG/raw），別用 JPEG —— 準確度與協定無關，但與「傳輸有無壓損」高度相關。

### 6. 是否應升級成避坑指南？

- [ ] 已驗證失敗　[x] 容易重複踩坑　[x] 未來應排除　[x] 對開發有約束價值

結論：部分（「proxy 端點改本機推論時，影像中繼不可留空 + 傳圖要無損」值得記）。
