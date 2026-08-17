---
date: 2026-07-14
type: reusable_flow
project: AIVision（.NET8 WPF 產線檢測 App）— API server
tags: [中央推論, API, 端點落地, 協定選型, Clean Architecture]
status: draft
candidate_prompt: true
candidate_sop: true
candidate_skill: false
---

# Reusable Flow - 2026-07-14

## Flow 1：把「既有辨識能力」暴露成中央推論 API 端點（Path B 落地流程）

### 1. 流程名稱

既有 Application 辨識器 → 中央推論 HTTP 端點（多 host 共用組件下的最小落地）

### 2. 觸發情境

已有一顆在別的 host（WPF）跑得好的辨識器/用例，要把它變成 server 端點供多台 edge 呼叫，且不想重寫辨識邏輯。

### 3. 流程步驟

1. **選協定**：熱路徑推論先用 **HTTP**（請求/回應語意最直接、點對點延遲最短、可重用既有 HTTP 適配器）；控制面（模型推播/遙測）才考慮 MQTT；效能瓶頸被實測證實後再上 gRPC。
2. **補 DI**：host（API）要註冊該辨識器依賴的所有 port（見 02_bug_notes Bug 1）。用「缺模型檔不拋」的辨識器建構子，讓未配模型也能啟動。
3. **定契約**：請求用 **multipart（png 無損 / raw+尺寸）**，回應直接對映領域觀測型別（本案 `PairObservation` + `modelVersion`/`elapsedMs`）。傳圖務必無損。
4. **真解碼**：端點自己把上傳影像還原成 `ImageData`（別沿用 proxy 的寬高留空，見 Bug 2）；raw 要校驗 buffer 長度。
5. **守鐵律**：server 只回**觀測（讀值+信心）**，**決策（三態/氣吹）永遠留 edge**。fail-closed（無物件/辨識失敗）走 **200**，只有請求壞掉/server 出錯才回 4xx/5xx（那才觸發 edge 降級）。
6. **端到端驗**：build 0 錯 → Development 啟動（過 `ValidateOnBuild`）→ Swagger 見端點 → 丟 PNG/raw 真圖 + 錯誤案例（缺 format/JPEG/raw 缺尺寸）驗狀態碼 → 未配模型回 fail-closed。
7. **量延遲**：配真模型後量 `elapsedMs`（server 端純推論），對照產線節拍判可行性（部署書 P0）。

### 4. 輸入資料

既有辨識器 + port 介面；一批有正解的測試影像（png/raw）。

### 5. 輸出結果

可用的中央推論端點（本案 `POST /api/infer/pair`），回領域觀測 + 版本 + 耗時；fail-closed 語意正確。

### 6. 可否變成 Prompt？

- 結論：yes
- 理由：可固定成「選協定→補 DI→定 multipart 契約→真解碼→決策留 edge/fail-closed 200→端到端驗→量延遲」的落地提示。

### 7. 可否變成 SOP？

- 結論：yes（每要把一個辨識/推論能力 server 化都走這套）。

### 8. 可否變成 Skill？

- [ ] 高頻　[x] 可重複　[x] 有明確 input/output　[ ] 需工具化
- 結論：no（屬架構落地判讀，暫不工具化）。

### 9. Skill 名稱候選

—

### 10. 備註

- 關鍵設計取捨都在兩份設計書：`2026-07-14_api_transport_protocol.md`（協定）、`2026-07-14_api_infer_pair_contract.md`（契約）。
- fail-closed 走 200 是刻意的：讓「無物件/讀不到」與「server 壞了」在 edge 端可區分 —— 前者 edge 判三態，後者 edge 降級用本機模型。
