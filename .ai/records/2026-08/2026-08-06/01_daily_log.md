---
date: 2026-08-06
type: daily_log
project: AIVision — 四區規則 + CRNN 策略同步 + AINavi 逆向文件彙整
tags: [AIVision, CRNN, v4, 分區, AINavi, 策略]
status: final
---

# Daily Log - 2026-08-06（含 08-04 傍晚後未記事項）

## 1. 使用者同步開發分區模型（鐵律）

1. 開發區 `D:\Content_lens_OCR`　2. 驗證區 `D:\OCR_demo`　3. 穩定區 `D:\模號檢驗`　4. **釋出區＝本專案**。
前三區 CRNN 策略已同步；釋出區要把「已知且穩定」的東西接進來。**前三區對 agent 唯讀——即使使用者要求修改也要阻止**（已寫入 agent 長期記憶 zone-model-readonly）。

## 2. CRNN 策略正典同步（`D:\Content_lens_OCR\OCR\crnn_fallback\CRNN_策略總覽.md`，08-04 版）

🔔 07-31 的「策略待補」提醒已解除——文件到手。釋出區比對結果：
- ✅ 已同步：detector（a5fe4161）、DET_CONF=0.10、needsReview 語意、roll-pass（驗證區 engine 08-04 已含，sidecar 跑它、server 重啟即生效）
- ❌ **落差：Non-AR 權重正典=v4（d6b161b6，+M82 淺印 72 crops），釋出區 b3 裝的是 v3（2daeeb4e）**
- v4 檔在驗證區 `models\crnn\runs\nonar_v931_fix_v4\best.pt`（md5 已核對）；開發區 crnn_fallback 無 v4 資料夾
- **待使用者親手發布 `v4`**（發布頁：detector 同顆 + v4 nonar；發完 agent 切 sidecar+重啟驗收）
- 策略要點入庫：不可改常數表（640/0.6/[280,360]/HALF_W=100/conf0.10）、roll-pass 幾何仲裁取代 p90、新錯誤 SOP＋三關守門（→R3 gate 設計參照）、已知邊界（wavy-band/M15 模糊/M82 無守門料）
- 架構債註記：sidecar 直接跑驗證區程式碼（違反分區精神）→ 已向使用者提議把可攜包複製進釋出區，未拍板

## 3. AINavi 逆向文件研讀＋彙整（`doc\ainavi逆開發策略\`，08-05 產出，狀態=討論中）

應使用者要求通讀並條列可參考方向（詳見對話輪彙整；重點）：
- **P0 借鏡**：①`ocr_2`=PaddleOCR＝字元式路線的外部佐證（對照試建議走開源 PaddleOCR 免授權摩擦）②`processor_id`＝sidecar「一行程掛多顆模型按 id 指定」——正中我們「按版本熱切換」待辦的答案形狀
- **P1 抄語意**：per-class 判定門檻進 _publish.json（解 CRNN 無 NG 類生產語意）、前處理參數 JSON 外部化納版控（消滅 train/infer 一致性痛點）、Port 檢查按鈕
- **不抄**：版本治理（我們明顯較好）、封閉 Import、多行程隔離（P-C session 池同效省資源）
- **紅線**：授權綁機（別在產線機/A1000 裝）、AinaviAdapter 不得綁 IAiInferencePort
- 待拍板三問：階段1 拋棄機錄 API 契約？3-1/3-2 算主項1延伸？對照試走 AINavi 還是開源？
- **使用者拍板：五項納入發展進度**→ ROADMAP 新增「AINavi 借鏡五項」checklist 區（①sidecar 多模型熱切換 ②per-class 門檻進 _publish.json ③前處理 JSON 外部化 ④Port 檢查鈕 ⑤PaddleOCR 開源對照試）；規則=完成即打勾+補日期。等同回答了 05 文件的拍板題：3-1/3-2 算主項1延伸、對照試走開源。

## 3.5 借鏡五項動工：①④ 當日完成（下午）

- **④ Port 檢查按鈕 ✅**：伺服器設定視窗「測連接埠」——純 TCP 探測（2s 逾時），訊息區分「埠開但 API 壞」vs「埠關=服務沒開/防火牆」。
- **① sidecar 多版本熱切換 ✅**：`CrnnSidecarService` 重寫為**按版本行程池**——每版本一子行程（SidecarInstance 自帶 gate；版本間不排隊）、檔案由登錄庫解析（`ResolveFile("ocr_crnn", v, ...)`＝版本治理同一套）、池上限 MaxProcesses=2 + LRU 淘汰閒置、`VERSION_NOT_FOUND:` 前綴→controller 轉 404。appsettings 改 `DefaultVersion`（廢 DetectorPath/NonarPath/VersionLabel）——**換版免改設定免重啟**。edge：CrnnInferClient 帶 modelVersion、CRNN 頁加「伺服器模型版本」下拉+查版本鈕（鏡射雙 head 頁）、健檢顯示預設版+池中版本。
- E2E 全綠：health cold/ready 池列表、預設版 b3 讀值 M101/08、指定 b3 共用行程熱 41ms、未知版 404 明確訊息。
- ROADMAP 借鏡五項：①④ 已打勾（2026-08-06）；②③⑤ 未動。
- 註：①正好是 v4 發布的最佳搭配——使用者發完 v4 後，CRNN 頁下拉選 v4 即可與 b3 並行對比（雙版本共存池）。

## 3.6 借鏡五項續：②③ 完成（晚間，使用者授權自主照表操課）

- **② per-class 門檻進 _publish.json ✅**：發布頁選填「模號/穴號信心門檻」（本地+server 雙層驗 0~1）→ `_publish.json` "judge" 段；CRNN 推論按**版本自帶門檻**重算 needsReview（覆蓋 sidecar 內建）並回傳套用值（reviewThresholdMohao/Xuehao 供 edge 對帳）。判定標準異動＝發新版，不改程式。
- **③ 前處理 JSON 外部化 ✅（最小子集）**：發布頁選填「前處理 JSON」→ "preprocess" 段（`UnmappedMemberHandling.Disallow`＝**鍵名打錯發布即 400**，不默默吞）；ocr_pair 指定版本推論用版本自帶 WarpPolarParams 建辨識器（GetPublishSection 帶 mtime 快取）。範圍：server 端 ocr_pair；CRNN 前處理在 sidecar 不適用；edge 本機仍 appsettings（後續）。
- E2E 全綠：judge 0.99→複檢觸發+門檻回聲、judge 1.5→400、preprocess 帶段推論 conf 1.0、錯鍵名 Imgz→400。測試版本 vtest-judge/vtest-pre 已清。
- 插曲：PS 5.1 傳 JSON 給 curl 的引號轉義坑 → 用 curl `-F "field=<file"` 語法繞過（記住這招）。
- ROADMAP 借鏡五項：**①②③④ 已完成**（皆 2026-08-06），剩 ⑤ PaddleOCR 開源對照試（規劃：獨立 venv 隔離安裝防污染 sidecar 環境；用 zone-2 前處理產 crops——唯讀 import 不改檔）。

## 3.7 借鏡五項收官：⑤ PaddleOCR 對照試完成（深夜）——五項全綠

兩階段隔離設計：A 段（系統 python）唯讀 import 驗證區前處理+同顆 detector 產 1061 對 crops（M101 全集；Hough 0 失敗/detector 1 失敗）；B 段（獨立 venv 只裝 rapidocr-onnxruntime=PP-OCR 的 ONNX 版，Apache-2.0）zero-shot 辨識。**輸入與 CRNN 完全同源→比的純粹是辨識器**。

**結果：模號 97.08%／穴號 39.30%／雙軸 37.70%，~344ms/crop(CPU)**。穴號崩潰形狀=短碼讀空或吐單個 0（04→空×62、08→0×57…）——通用 rec 對極短無語境短碼天生弱。
**結論**：①字元式路線再佐證（模號 zero-shot 就 97%）②**CRNN 針對性設計有真實價值**（穴號 39 vs 99.98）→ 不需為 ocr_2 談 AINavi 授權、被開源即插即用取代風險低 ③延遲也輸（~700ms/張 vs ~100ms）④fine-tune 能追但=重投訓練成本，CRNN 已用同成本拿到 99.98，不值得。
報告：`experiments/paddleocr_compare/REPORT_三方對照.md`（含逐筆 results.jsonl；venv 可整夾刪）。
**ROADMAP「AINavi 借鏡五項」①②③④⑤ 全部完成打勾（皆 2026-08-06）**。

## 3.8 Boss 要求審核複查 → 驗證清單制度化（收官）

Boss 指正「別只說全綠，先自審+複查再回報」→ 對五項做對抗性複查，**抓到兩個初驗證明力不足的地方並補證**：
- **①多版本共存其實沒測過**（初驗只有單版本）→ 補測：雙版本共存池、共存不互踢（b3 保持熱 38ms）、第三版進池 LRU 正確踢「最久未用」——全過。
- **③初驗用了與 baseline 相同的參數，conf 1.0 證明不了採用路徑** → 補測：發布「必壞參數」版（HoughMinRadius=10）→ 同檔案壞參數版 hough miss、正常版 conf 1.0——證明版本參數真的生效。
- 其餘複查：②單邊門檻/無段回退 ✓、④TCP 開關兩案+XAML↔VM 綁定 grep 全對 ✓、⑤獨立重算 1061/97.08/39.30/37.70 與報告一致+錯例抽查合理 ✓、⑤報告補「未含 roll-pass」誠實註記。
- 產出 **doc/2026-08-06_借鏡五項_驗證清單.md**（主張→驗法→證據→初驗/複查狀態），並誠實列**未驗項**：UI 點擊層（綁定已核、後端已測，剩視覺層）、①並發極端 race 未壓測（掛 P-C 一併）、⑤不能外推 fine-tune。
- 測試產物六個 vtest-* 全清；server 乾淨重啟（ready，CRNN 池空屬正常）。
- **教訓（制度化）**：初驗（快樂路徑）≠合格；「差異化輸入證明採用路徑」「獨立重算不信自印」「綁定 grep 對照」是低成本高價值的複查三招——之後每個交付照此辦理再回報。

## 3.9 第二輪深掃（Boss「全部都有考慮到？」）——又抓到兩個並修掉

- **sidecar 曾寫 .pyc 進驗證區**（08-04 首跑 b3 時的 import 副作用，違四區唯讀字面規則）→ sidecar `-B`+環境變數雙保險、實驗腳本 `sys.dont_write_bytecode`；重啟實查命令列含 `-B` ✅。教訓入 agent 記憶（zone-model-readonly 更新）。
- **HANDOFF_API.md 過期**（教改已廢的 DetectorPath 換版本、未涵蓋五項/四區）→ §0 重寫。
- 孤兒 sidecar 疑慮排除（stdin EOF 自退，實查無孤兒）。
- 驗證清單補「第二輪深掃」節＋既列未解提醒（無認證/磁碟配額/RAM 預算/TimeBudgetMs）。

## 4. 待辦

- 🔔 **使用者發布 CRNN v4**（最高優先；發完 agent 切 sidecar 驗收 d6b161b6）
- CRNN 頁使用者點測；可攜包進釋出區拍板；AINavi 三問拍板
- 沿用：M83 雙引擎對比、A1/a100 盤點、vtest-0731 刪、R3/R4、P-B/P-C、安全地基
