---
date: 2026-08-10
type: daily_log
project: AIVision — 自我盤點文件 + 與 AINavi 發展比較
tags: [AIVision, AINavi, 比較, 文件, lunglungNavi, 發展比較]
status: final
---

# Daily Log - 2026-08-10（第二篇）

## 1. 今日主題

把我方（AIVision）當下的發展與策略，比照 `doc/ainavi逆開發策略/` 的結構製作成對照文件，再做雙方比較。使用者三點需求：①我方文件對齊 AINavi ②我方與 AINavi 比較 ③紀錄比較文件。

## 2. 產出

### 2.1 `doc/lunglungNavi/`（我方自我盤點，對齊 AINavi 01–05）
- README（三句話+五問速答，說明 lunglungNavi=AIVision 對照代號）
- 01_架構與技術棧（三段式拓撲、.NET 分層、技術棧對比表、API 契約、CRNN sidecar）
- 02_如何使用與部署（產線熱迴圈/批量試模/發布頁三情境、部署方式、**無授權設計**、安全地基債）
- 03_引擎策略與模型治理（雙head→CRNN、前處理、task 化、版本狀態機、發布 gate、隔離試模、借鏡五項、四時機五驗證信任鏈）
- 04_多站並行與實時降級（串行鎖 356/619/864ms、P-B/P-C、自動降級、fail-closed、節拍矛盾、四格矩陣）
- 05_發展路線與待辦（三主項現況、借鏡五項、待辦分類、風險、AINavi 的意義）
- 註：AINavi 06–09 是現場採集專屬，我方無對應物（README 已說明）。

### 2.2 `doc/發展比較/`（比較＋紀錄）
- README（計分卡：12 面向，我方 6 領先/AINavi 6 領先；定位光譜/架構/OCR 三張圖）
- 01_逐面向對比（A~I 九面向，每項「雙方做法→各自優勢→本質差異」+ 一頁彙總）
- 02_結論與行動（守4/補5/借鏡4/不做4；產品定位岔路留使用者拍板；不新增 ROADMAP 主項）

## 3. 核心結論（誠實對稱）

- **不是同類產品**：AINavi 廣（一站式 AI 平台）、我方深（產線推論+版控）。計分 6:6。
- **我方領先**：版本治理（狀態機/md5/回滾/零退步 gate，AINavi 部署層連版本號都沒有）、信任鏈、隔離效率（同行程 session 省 1-2 數量級）、模型可攜、產線閉環（工單防呆/fail-closed/不停線）、OCR 針對性。
- **AINavi 領先**：多站並行（一模型一行程天生免串行鎖＝我方最大硬傷）、功能廣度（標/訓/驗/部署/anomaly/defect 一條龍）、產品成熟度（Downloader/服務化/授權/多人 Web）、安全地基、GPU 產品化。
- **OCR 同走字元式**（PaddleOCR vs CRNN），印證 08-04 拍板方向。

## 4. 事實來源（避免杜撰）

本 repo 只有文件無原始碼；我方事實全部取自 ROADMAP.md 與 .ai/designs/（api_server_deployment、api_infer_pair_contract、model_release_and_trust、crnn_engine_intake、multi_model_server_architecture）+ doc/2026-08-06_借鏡五項_驗證清單.md。動筆前已逐份讀齊。

## 5. 待辦 / 未決

- 🅰 **產品定位岔路待使用者拍板**（比較篇 02 §6）：維持 WPF 產線工具 vs 轉一站式 Web 平台 → 決定 AINavi 是純參照還是要另立計畫。拍板前不動 ROADMAP。
- 比較篇建議的「補」：P-B/P-C 多站並行（思潔那台 A1000 若能借測最省）、安全地基、server 服務化——都掛在既有主項下，非新主項。
- 🔔 沿用：CRNN 策略文件使用者仍未給（主動提醒）。
- 沿用：M83 整夾、R3/R4、人工 R1/R2、TimeBudgetMs 矛盾、pairs A1/a100 盤點、vtest-0731 待刪。

## 6. 一句話總結

三組文件成套（ainavi逆開發策略／lunglungNavi／發展比較）：AINavi 廣、我方深，六比六；當務之急不是變成它，是補多站並行硬傷與安全地基硬債。
