---
date: 2026-07-14
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: Edge↔Server 推論通訊協定選型與演進（HTTP → MQTT → gRPC）
status: decision（方向已定：起步走 HTTP；MQTT/gRPC 為後續演進）
tags: [API, 通訊協定, HTTP, MQTT, gRPC, 中央推論, 延遲, 準確度]
---

# 設計紀錄：Edge↔Server 推論通訊協定選型與演進

> 場景（2026-07-14 討論）：edge 端做完前處理後，**丟一張圖給 server → server 跑雙 head ONNX → 回讀值+信心**。需求優先序：**速度（節拍內來得及）＋ 準確度**。
> 本文件記錄「HTTP / MQTT / gRPC 三者比較」與「逐漸發展」的演進路線。承接 `2026-07-12_api_server_deployment.md` §2.5 中央推論設計。

---

## 0. 一句話結論

**核心推論熱路徑起步走 HTTP**（原生請求/回應、點對點最短延遲、且程式已有 HTTP 適配器可重用）。
**MQTT 不用在推論熱路徑**，而是留給「控制面」（模型版本推播、edge 遙測/降級告警）。
**gRPC 是日後追求更高吞吐/更低 RPC 開銷時的升級選項**，屬 HTTP 家族內的效能演進，非起步必需。

> ⚠️ 重要澄清：**準確度與選哪個協定無關**。三者底層都是 TCP、位元組都完整送達。真正會傷準確度的是「對圖做有損壓縮（如 JPEG）」或「丟幀」——那是 payload/QoS 的問題，不是協定之爭。**保準確度的鐵律：圖用無損（原始 bytes 或 PNG）傳。**

---

## 1. 這個 case 的本質：請求/回應（RPC），不是串流/廣播

「送這張圖、等**這張圖**的答案」是一來一回的同步呼叫。這個形狀決定了選型：

- **HTTP**：天生請求/回應。一個 POST 帶圖、response 帶結果，一次來回搞定。
- **gRPC**：也是請求/回應（RPC），但走 HTTP/2 + protobuf，序列化與連線多工更省。
- **MQTT**：是 pub/sub 訊息匯流排，**不是**請求/回應。要硬湊 req/resp 得自己管 correlation id、雙向 topic、broker 中繼——用非同步廣播工具去湊同步呼叫，不對味。

---

## 2. 三者比較

| 面向 | **HTTP（起步推薦）** | **gRPC（效能升級）** | MQTT（控制面才用） |
|---|---|---|---|
| 通訊模式 | 原生請求/回應 ✅ | 原生 RPC ✅ | pub/sub，要自湊 req/resp ❌ |
| 延遲（單次） | 點對點直連，短 ✅ | 點對點 + HTTP/2 多工，最短 ✅✅ | 經 broker 多兩跳，最長 ❌ |
| 序列化開銷 | JSON/multipart，中 | protobuf 二進位，低 ✅ | 自訂 payload |
| 大二進位圖 payload | 適合（multipart/raw body）✅ | 適合（bytes field / stream）✅ | broker 常有大小限制、非為大 blob 設計 ⚠️ |
| 逾時/降級/重試 | 直觀（timeout + catch）✅ | 有 deadline/攔截器 ✅ | 需自管狀態機 ⚠️ |
| 額外元件 | 無 ✅ | 無（自帶 server）✅ | 要多維運一個 broker（潛在單點）❌ |
| 生態/工具 | 最成熟、Swagger 可點 ✅ | 需 .proto + 產碼；瀏覽器直連受限 ⚠️ | IoT 遙測生態強 |
| 既有程式重用 | ✅ `AinaviAiInferencePort` 現成 | 需新增 gRPC service | 無 |
| 準確度 | 無差別 | 無差別 | 無差別 |

**速度直覺**：同網段下單次推論延遲被 GPU 推論本身（數十 ms）主導，傳輸不該再加無謂中繼。HTTP 用 keep-alive/HTTP2 省掉每次握手後，傳輸開銷已極低；gRPC 再進一步壓序列化與多工；MQTT 的 broker 中繼反而是逆向操作。

---

## 3. 逐漸發展的演進路線

> 原則：**先用最省力、最直觀、已有現成程式的方案讓路打通；有實測數據證明需要時，才往效能演進。不要為想像中的吞吐先上重工具。**

### 階段 1（起步）— HTTP：把中央推論路打通
- 新增 `POST /api/infer/pair`：收前處理圖 → 補真影像解碼 → 跑雙 head → 回 `{mohao, xuehao, confMohao, confXuehao}`。
- Edge 端沿用既有 HTTP 適配器（`AinaviAiInferencePort` / `SwitchableAiInferencePort`），指向自建端點。
- 連線用 **keep-alive / HTTP/2**，避免每顆重握手。
- 圖用**無損**傳輸（原始 bytes 或 PNG）。
- **產出**：一台 edge 可走中央推論、讀值正確。對應部署書 §8 的 **P1**。
- **量測**：單張 server 推論延遲（含網路）vs 節拍——這個數字決定要不要進階段 3。

### 階段 2（並行）— MQTT：控制面（非推論熱路徑）
- **不動推論路徑**。MQTT 用在 pub/sub 對味的地方：
  - **模型中樞推播**：server 有新 stable 版本 → 廣播給所有 edge「該拉新模型」。
  - **edge 健康/降級遙測**：heartbeat、降級告警、節拍統計上拋集中觀測。
- 這些不在毫秒級節拍關鍵路徑上，可非同步、可容忍延遲。
- **觸發條件**：接了多台 edge、需要集中觀測/主動下發時才加；單機階段可先不做。

### 階段 3（效能升級，條件觸發）— gRPC：更高吞吐/更低延遲
- **觸發條件**：階段 1 實測發現 HTTP/JSON 序列化或連線開銷成為瓶頸（多線共用、高頻節拍時）。
- 把 `infer/pair` 改/加一條 gRPC service（HTTP/2 + protobuf），圖走 bytes field，必要時用 client-streaming 送多幀批次。
- 保留 HTTP 端點供 Swagger 除錯/相容。
- **不是起步必需**：沒有實測瓶頸就別先上，proto + 產碼是額外維護成本。

```
階段1 HTTP（打通熱路徑，量測延遲）
   │  ├─ 若需多台集中/主動下發 → 階段2 MQTT（控制面，並行不衝突）
   │  └─ 若實測 HTTP 開銷成瓶頸 → 階段3 gRPC（熱路徑效能升級）
   ▼
持續：熱路徑 = HTTP/gRPC 擇一；控制面 = MQTT（可選）
```

---

## 4. 決策摘要（給下一個 session）

1. **推論熱路徑起步 = HTTP**，沿用既有適配器，最省力也夠快。
2. **準確度不靠協定**，靠「無損傳圖 + 模型本身」；別把準確度當協定選型依據。
3. **MQTT ≠ 推論路徑**，只在「模型推播 / 遙測」等控制面才考慮，且多台接入時才需要。
4. **gRPC 是條件觸發的效能升級**，要有階段 1 的實測瓶頸數據才動。
5. 仍待拍板的兩個數字（影響是否需要進階段 3）：**產線節拍**、**server 有無 GPU**。

---

## 5. 檔案地標
- 本紀錄：`.ai\designs\2026-07-14_api_transport_protocol.md`
- 中央推論/模型中樞大方向：`.ai\designs\2026-07-12_api_server_deployment.md`（§2.5 中央推論、§2.6 模型中樞）
- 本地啟動指南：`.ai\designs\2026-07-12_api_local_run_guide.md`
- API 交接：`.ai\HANDOFF_API.md`
- 可重用 HTTP 推論適配器：`AIVision.Infrastructure\AiService\{AinaviAiInferencePort,SwitchableAiInferencePort}.cs`
- Path B 要接的真辨識器：`AIVision.MoldCode.Onnx\SwitchableTwoHeadRecognizer.cs`
