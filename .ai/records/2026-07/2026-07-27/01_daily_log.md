---
date: 2026-07-27
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— API server 測試執行
tags: [API, 測試, infer/pair, fail-closed, Passes, 延遲, 測試基準]
status: draft
---

# Daily Log - 2026-07-27

## 1. 今日主題

基於通用測試基準 `doc/TEST_PLAN_BASELINE.md` 產出 **API server 測試計畫**（`doc/api_test/API_TEST_PLAN.md`，21 卡），並**實跑一輪**、回填快照（`doc/api_test/API_TEST_RUN_2026-07-27.md`）。核心功能全過、找出兩個 legacy 端點的健壯性問題、並用大樣本驗證了 Passes=1。

## 2. 進度

- **產出 API 測試計畫**：沿用基準卡片四大欄位（項目/內容/預期+實際/預期UI+實際UI）；因 API 無畫面，第 4 欄「UI 導向」轉譯為「**API 介面/開發者體感**」（Swagger 可探索性、契約直覺、錯誤訊息清晰、狀態碼語意），結構不刪。涵蓋建置/啟動/Swagger/健檢/推論正常/錯誤邊界/延遲/legacy/Edge整合/安全 共 21 卡。
- **實跑（Release、模型 v671、Development）21 卡 → Pass 16 / Fail 2 / 待人工 2 / 現況風險確認 2**：
  - 健檢 ready(20/18)、**degraded 亦回 200**（隔離試模基礎正確）。
  - `infer/pair` PNG & raw 皆讀 **M101/08 conf 0.99999988**；空白圖/未配模型 **fail-closed 200**（訊息指路）。
  - 錯誤碼精準：缺 image/format→400、**JPEG→415**、raw 缺尺寸/長度不符→400（長度訊息含「需 1044000 / 實得 3039」）。
  - Production：`/swagger` 404、API 200（環境切換正確）。
- **大樣本一致性（180 張 M101×18 穴，每穴 10 張，精準重現 07-15 基準）**：
  - **Passes=1：雙軸 180/180 @ p50 136ms**；Passes=2：179/180 @ p50 262ms（唯一誤：穴號 13→03，conf1.00/0.96）。
  - 結論：本樣本 **Passes=1 又快近一倍、又不遜於 Passes=2** → 回應 ROADMAP 風險「Passes=1 未大樣本驗證」（惟跨模號超大樣本仍建議再跑 M83 整夾）。
- **兩個 legacy 發現（見 02_bug_notes）**：`ainavi/predict` EdgeHub 不可達時 hang>15s；`Inspection/cycle` 回 500 且 Development 洩漏完整 stack trace。

## 3. 今日重要決策 / 觀察

- **測試基準的「UI 欄位」對無介面服務要轉譯成「介面/開發者體感」**，才不會硬套 —— 已寫進 API 計畫 §0，可作為日後其他「非 UI 元件」測試的通則。
- **Passes=1 值得考慮設為預設**（快近一倍、本樣本零退步）；但 edge `TimeBudgetMs=120` 與單幀延遲的矛盾仍未解，且需跨模號超大樣本背書後再定。
- legacy `ainavi/*`、`Inspection/cycle` 屬非主項（EdgeHub 線），但「hang / 洩漏堆疊」是通用健壯性問題，已記錄待修。

## 4. 產出

- 新增 `doc/api_test/API_TEST_PLAN.md`（21 卡，API 測試模板）。
- 新增 `doc/api_test/API_TEST_RUN_2026-07-27.md`（單輪執行快照，含數據與發現）。
- 本日 records（01/02）+ `status.json` 同步 + 儀表板重生。

## 5. 今日一句話總結

依通用基準產出並實跑 API server 測試（21 卡 Pass16/Fail2）：核心 infer/pair 讀值 conf≈1.0、錯誤碼與 fail-closed 全對、Production 環境正確；大樣本重現 Passes=1 180/180 @136ms（又快又不遜）；揪出兩個 legacy 端點健壯性問題（ainavi hang、Inspection 洩漏堆疊）。
