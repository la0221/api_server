---
date: 2026-08-04
type: daily_log
project: AIVision — CRNN 發布閉環（使用者親手）+ server 啟動工具
tags: [AIVision, CRNN, 發布, sidecar, b3]
status: final
---

# Daily Log - 2026-08-04

## 1. 今日主題

CRNN 的「發布→上架→server 載入→推論」閉環，**全程由使用者親手完成**（07-31 教訓的正確落地）。

## 2. 進度

- **server 連不上排查**：機器重開後 API server 沒了（非常駐服務）→ 建根目錄 **`啟動API伺服器.bat`**（雙擊起 server），`使用流程_中央推論.md` §1 補說明。
- **使用者親手發布 CRNN**：發布頁用途=CRNN → 過程兩個真實卡點都被 UI 防呆接住：①nonar.pt 欄漏選（擋下並點名）②detector 欄誤放 nonar 權重（靠檔案大小 6MB/13MB 對照講解修正）→ 成功上架 **`ocr_crnn/b3`**（detector md5=a5fe4161／nonar md5=2daeeb4e，與 production 權重一致）。
- **sidecar 切到 b3**：appsettings CrnnSidecar 改指 `ocr_crnn\b3` + VersionLabel=crnn-b3 → 重啟 server → 推論驗收 **M101/08 conf 0.953/0.963、版本回聲 crnn-b3、health ready** ✅。
- **附帶發現**：pairs 登錄夾出現 `A1`、`a100`（使用者這幾天自行用發布頁上架的雙 head 版本）＝發布流程已上手；`vtest-0731` 仍待刪。
- 使用者疑問澄清：雙head頁看不到 CRNN 屬正常（不同用途/引擎，那頁只列 pairs）；CRNN 的 edge 測試 UI 在待辦。

## 2.5 引擎策略拍板 + CRNN 專屬測試頁（下午）

**使用者拍板：CRNN 效果強於雙 head，將逐步取代；現階段並行**——已錨進 ROADMAP 背景區。據此完成主控的 CRNN 入口：

- `Infrastructure\MoldCode\CrnnInferClient.cs`（新）：health（不觸發冷啟）+ 推論（PNG multipart；逾時 120s 容 sidecar 冷啟；傳輸失敗 vs 有效觀測分明）。
- **新頁「CRNN 測試（中央推論）」**（選單：面板→模型與測試→CRNN 測試；IsEngineerOrAbove）：健檢→選資料夾（慣例同雙head頁：夾名=模號正解/子夾=穴號正解、TestFolderOptions 下拉共用）→整批走 `POST /api/infer/ocr_crnn` →結果表含**建議複檢**欄（CRNN 無 NG 類的品質旗標）→報告=準確率+複檢數+來回/sidecar p50/p95。連續 3 次傳輸失敗中止；jpg 本地無損轉 PNG。
- CRNN 只在 server 跑（無本地版）→ 本頁一律走中央，頁面上明示。

## 2.6 使用者三點回饋（傍晚）

1. **UI 參考雙 head**：CRNN 頁已重排為鏡射雙 head 版面（①狀態區/②工具列/辨識過程帶/結果表/狀態列同構；差異只在無本地版、無版本載入區、多「建議複檢」欄）。
2. **CRNN 策略≠雙 head 策略**：CRNN 模型用了其他策略，不能照抄雙 head 的成功策略——現行頁面的評分/正解慣例（夾名=正解等）是**暫沿雙 head 的佔位版**。
3. 🔔 **使用者會整理 CRNN 策略後補來，要求 agent 主動提醒**——已寫入 status.json handoff 第①條（開場必提醒），拿到後回頭調整 CRNN 頁的評分/流程。

## 3. 待辦 / 未決

- CRNN edge 測試 UI（來源/引擎選擇）、sidecar 按版本熱切換、CRNN 定位拍板（無 NG 類→needsReview 語意）。
- 盤點 pairs 的 A1/a100（來源/md5 溯源+隔離試模驗證）；刪 vtest-0731。
- server 開機自動啟動（工作排程器）——已提議未拍板。
- 沿用：M83 整夾、R3/R4、人工 R1/R2、P-B/P-C、TimeBudgetMs 矛盾、安全地基。

## 4. 一句話總結

發布這件事從「agent 示範」變成「使用者日常」——CRNN b3 親手上架、防呆真的接住了兩次選檔失誤，工具算是及格了。
