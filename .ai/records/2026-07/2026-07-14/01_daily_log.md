---
date: 2026-07-14
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— API server 中央推論打通
tags: [AIVision, API, ASP.NET Core, 中央推論, 雙head, ONNX, DI, infer/pair, 協定選型]
status: draft
---

# Daily Log - 2026-07-14

## 1. 今日主題

打通「API server」這條線：先讀完既有交接＋兩份設計書，選定 edge↔server 通訊協定，再直接動工把中央推論端點 `POST /api/infer/pair` 建起來，並解掉 API「一啟動就崩」的 DI 缺註冊。**build 0 錯、Development 可啟動、端點端到端實測通過。**

## 2. 今日完成事項

- **文件閱讀**：讀通 `HANDOFF_API.md` + `2026-07-12_api_server_deployment.md` + `2026-07-12_api_local_run_guide.md`，對照實際 code 核實現況（4 endpoint、DI 崩因、In-Memory、無認證）皆與文件一致。
- **通訊協定選型（新設計書）**：`2026-07-14_api_transport_protocol.md`。結論：推論熱路徑走 **HTTP**（原生請求/回應、點對點最短延遲、可重用既有 `AinaviAiInferencePort`）；**MQTT** 只留控制面（模型推播/遙測）；**gRPC** 為條件觸發的效能升級。釐清「準確度與協定無關，靠無損傳圖」。
- **API 契約（新設計書）**：`2026-07-14_api_infer_pair_contract.md`。定義 `POST /api/infer/pair` 的 multipart 請求（png/raw + 尺寸中繼）、回應（對映 `PairObservation` + `modelVersion`/`elapsedMs`）、fail-closed 語意與錯誤碼；對齊真實型別 `ImageData`/`PairObservation`。
- **實作 Path B（改 code）**：
  - `AIVision.Api.csproj` 加參考 `AIVision.MoldCode.Onnx`。
  - `MoldCodeImageLoader` 新增 `LoadFromBytes()`（同 `Cv2.ImDecode` 路徑，前處理對齊）。
  - `Program.cs` 註冊單/雙 head 兩個真辨識器 + options → **解掉啟動即崩**（`IMoldCodeRecognizerPort` / `IMoldCodePairRecognizerPort` 過去只在 WPF 註冊）。
  - 新增 `InferController`：`POST /api/infer/pair`，含 png/raw 解碼、raw 長度校驗、禁 JPEG（PNG 魔術位元組）、耗時量測、模型版本回填。修掉現況 `ImageData` 寬高寫死 0。
  - `appsettings.json` 加 `MoldCodeWarpPolar`/`MoldCodeOnnx` 區段（留空亦能啟動）。
- **實測驗證**：build 0 錯；Development 啟動成功（`ValidateOnBuild` 過關＝DI 崩潰已解）；Swagger 見 `/api/infer/pair`；PNG 真圖 + raw 皆成功解碼跑辨識器，未配模型回 fail-closed 200（`hasReading:false` + 原因）；錯誤案例（缺 format→400、送 JPEG→415、raw 缺尺寸/長度不足→400）全數符合契約。

## 3. 今日重要決策

- **推論協定＝HTTP 起步**，MQTT 不進熱路徑、gRPC 待實測瓶頸再升級（見協定設計書 §0）。
- **註冊兩個真辨識器而非 stub**（Path A）：啟動需同時解析單/雙 head 兩 port，兩者建構子「缺模型檔只記錄、不拋」，直接上真辨識器最乾淨，也順帶讓 `Inspection/cycle` 可用。
- **只註冊辨識 port，不註冊 UI-only 的執行期切換 port**（`IMoldCodePairModelSwitch` 等 API 不需要）。
- **fail-closed 走 200 不走錯誤碼**：NO OBJECT / 辨識失敗是有效觀測，交 edge 判三態；4xx/5xx 只留給請求壞掉/server 出錯（才觸發 edge 降級）。

## 4. 今日改動摘要（AIVision）

- Api：`AIVision.Api.csproj`（+MoldCode.Onnx 參考）；`Program.cs`（註冊雙辨識器+options）；`Controllers/InferController.cs`（新增 infer/pair + 請求/回應 DTO）；`appsettings.json`（+MoldCodeWarpPolar/MoldCodeOnnx）。
- MoldCode.Onnx：`MoldCodeImageLoader.LoadFromBytes()`。
- 文件：`.ai/designs/2026-07-14_api_transport_protocol.md`、`.ai/designs/2026-07-14_api_infer_pair_contract.md`；更新 `HANDOFF_API.md`。

## 5. 尚未完成 / 明日接續

- **配真模型驗讀值 + 量單張延遲**：把 `MoldCodeWarpPolar` 兩個 `.onnx` 路徑指向實體檔 → 用 Swagger/curl 丟已知圖驗讀值、量 `elapsedMs`（部署書 P0 的可行性數字）。
- **兩個待拍板數字**：產線節拍、server 有無 NVIDIA GPU（決定是否需 gRPC/GPU）。
- **GPU 版 OnnxRuntime**（有 GPU 時）。
- **edge 端接線**：既有 HTTP 適配器指向 `/api/infer/pair` + 逾時降級本機（部署書 P2）。
- 後續：模型倉庫/發佈 API（P3）、安全（TLS/authn）、機密外移、持久化（PostgreSQL/SQL Server）。

## 6. 今日一句話總結

API server 從「一啟動就崩」推進到「Development 可啟動、中央推論端點 `POST /api/infer/pair` 端到端跑通（fail-closed 正確）」；協定選型定為 HTTP 起步、MQTT 控制面、gRPC 條件升級；下一步配真模型量單張延遲驗可行性。
