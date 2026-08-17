---
date: 2026-07-27
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）— API server
tags: [API, HttpClient, 逾時, 例外洩漏, EdgeHub, 健壯性]
status: draft
promote_to_pitfall: true
---

# Bug Notes - 2026-07-27（API 測試揪出）

> 皆在 legacy / EdgeHub 線（ROADMAP 列非主項）；優先度低，但屬通用健壯性問題，記錄待修。

## 坑 1：`ainavi/predict` 對 EdgeHub 無連線逾時 → 不可達時 hang >15 秒

### 1. 錯誤情境
API 測試 API-LG-02：EdgeHub（192.168.1.95:8001）不在線，呼叫 `POST /api/ainavi/predict`。

### 2. 錯誤現象
請求**掛起 >15 秒**（curl `-m 15` 逾時，HTTP 000），非 fast-fail。呼叫端無法區分「慢」與「壞」。

### 3. 最終原因
`AinaviAiInferencePort` 的 `HttpClient` 未設 `Timeout`（或過長），連線對象無回應時走 TCP 預設逾時（數十秒）。

### 4. 建議解法（未修）
給該 HttpClient 設合理 `Timeout`（如 2–3s）+ 明確錯誤訊息；或健檢先探測 EdgeHub 可達性再送。

### 5. 下次先檢查
- 任何「轉發外部服務」的端點，其 HttpClient 是否設逾時？外部不可達應 fast-fail 回可讀錯誤，不可 hang。

### 6. 升級避坑指南？
- [x] 已驗證　[x] 易重複（所有外呼端點通病）　[x] 應排除　—— yes（HttpClient 一律設逾時）。

---

## 坑 2：`Inspection/cycle` 硬綁不可達 EdgeHub → 500，且 Development 洩漏完整 stack trace

### 1. 錯誤情境
API 測試 API-LG-01：`POST /api/Inspection/cycle` body `{}`。

### 2. 錯誤現象
**HTTP 500**；body 為 DeveloperExceptionPage 的**完整例外堆疊**（含 `D:\新增資料夾\...` 原始碼路徑、每層 frame）。根因是 handler 走 `AinaviAiInferencePort` 連 EdgeHub 8001 失敗。

### 3. 最終原因
- 端點名為「Inspection/cycle」卻硬綁 AINAVI EdgeHub 推論（呼叫端不會預期此依賴）。
- Development 環境開 DeveloperExceptionPage → 例外細節全洩漏。

### 4. 建議解法（未修）
- 對「外部依賴不可達」回**可讀的錯誤 DTO**（狀態 + 訊息），而非原始堆疊。
- **Production 務必確認關閉** DeveloperExceptionPage（勿把堆疊/路徑對外）。

### 5. 下次先檢查
- Production 有無把詳細例外對外？（資安/資訊洩漏）
- 端點的隱性外部依賴是否在契約/命名反映出來？

### 6. 升級避坑指南？
- [x] 已驗證　[x] 易重複　[x] 應排除　[x] 對決策有約束（資安）—— yes。

---

## 附：非 bug 但記錄——未載入模型時 `objectPresent=true`
未配模型時 `infer/pair` 回 `objectPresent=true` 但 `hasReading=false`（failureReason 指路）。語意小瑕疵（無物件卻 present=true），因 hasReading=false 仍 fail-closed 安全、影響低；若日後嚴謹化可讓「無模型」時 present=false。
