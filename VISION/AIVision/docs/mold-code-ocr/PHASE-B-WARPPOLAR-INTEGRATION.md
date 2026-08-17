# Phase A/B — V6.7.1 warpPolar+annulus 雙 head 接進 AIVision C# 產線

> 狀態：**Phase A/B code 完成、全綠**（C# 推論對齊 Python、48 單元測試過、cycle demo 過、Codex hostile review 過並 triage）。
> 建立：2026-06-22 ｜ 宿主：`ai_vision`（AIVision，Clean Arch .NET8）
> 來源模型：`G:\隱眼專案`（V6.7.1，yolov8s-cls ×2：mohao 20 類含 NG、xuehao 18 類）

---

## 1. 背景與決策

`G:\隱眼專案` 的 V6.7.1 用 **warpPolar 攤平 + annulus 去內圈** 前處理 + **雙 head**（模號 mohao / 穴號 xuehao 各一 yolov8s-cls），與 AIVision 原本的 **blackhat 單 head**（只分穴號、模號靠人輸入）完全不同。

→ 依 `decision-lifecycle.md`：**warpPolar+annulus 路線取代（supersede）blackhat baseline**（對應 0616 記憶定論「封閉集用攤平→分類」）。新辨識器與舊 blackhat 路徑**並存**（charter「辨識器可抽換」），舊路徑不動。

annulus 去內圈是**真泛化改善**（治 shortcut-learning：模型原本偷看內圈花紋當假特徵，純藍 M101 遇花紋片誤判 M59/M50）。獨立 mohao head **補上** 06-11 §9C 的 fail-open 漏洞（模號之前全靠人輸入對）。

## 2. 新增/修改檔案

**Phase A（前處理 + 推論，Onnx 專案）**
- `WarpPolarPreprocessor.cs` ★ — Hough→warpPolar→annulus[0.6r,r]→flip/transpose→white-pad640；RGB/255 張量；ROI 裁切（config-driven）
- `WarpPolarTwoHeadRecognizer.cs` ★ — 載兩 ONNX、2-pass 接縫投票、類別名讀 ONNX metadata；實作 `IMoldCodePairRecognizerPort`
- `MoldCodeWarpPolarOptions.cs` ★

**Phase B（決策 + 編排）**
- Domain：`PairObservation` / `PairVerifyOutcome`(+Reject) / `PairDecision` / `MoldCodePairVerifier`(分軸三態) / `MoldCodePairVoter`(配對加權投票) ★
- Application：`IMoldCodePairRecognizerPort` / `MoldCodePairCycleOptions` / `VerifyMoldCodePairCycleCommand(+Result+Handler)` ★
- WPF：`App.xaml.cs` DI 區 + `appsettings.json`（`MoldCodeWarpPolar` / `MoldCodePairCycle` 區）
- Harness：`golden`（golden test）、`paircycle`（cycle demo）模式
- Tests：`MoldCodePairVerifierTests`(24) / `MoldCodePairVoterTests` / `MoldCodePairCycleHandlerTests`

## 3. 驗證（golden test）

C# 推論 vs Python engine（同一批 680 張、同一 ONNX）：
- present **680/680 一致**；xuehao **680/680 label 一致**，max Δconf **0.0001**（近 bit-exact）
- mohao **679/680 一致**；唯一 1 張（M82/16）C# 與 Python **都判錯**（模型知識邊界，非 port 錯）
- C# 端絕對準確率：mohao 99.71% / xuehao 99.85% / both 99.56%

工具：`VISION/AIVision/tools/golden-test/`（`golden_dump.py` + Harness `golden` 模式）。

## 4. 分軸三態決策（對齊 Python reconcile）

| 條件 | 結果 | IO |
|---|---|---|
| 兩軸都 == 預期 | Match | 放行 Result(OK) |
| 任一軸 != 預期且該軸高信心（模號≥0.60 / 穴號≥0.85） | MixedAlarm | 氣吹 Blow |
| 模號 head 判 NG 且高信心 | Reject | 氣吹 Blow |
| 有軸不符但低信心（模型搖擺） | TrustInput | 放行 Result(OK) |
| 無物件 / 辨識失敗 / 信心非有限 / **低信心 NG** | Skip | **Result(NG)（fail-closed，不放行）** |

門檻分軸理由：模號粗特徵可靠→嚴格（不同模具盡量抓）；穴號 11↔17 搖擺→寬容（低信心採信操作員）。

## 5. Codex Hostile Review（2026-06-22，round 1）+ Triage

| # | 嚴重度 | Finding | 處置 | 對應修正 |
|---|---|---|---|---|
| 1 | Blocker | 低信心 NG 落入 TrustInput→放行（defect 逃脫） | **Adopt** | `MoldCodePairVerifier`：NG 一律不放行（高信心 Reject / 低信心 Skip） |
| 2 | High | Live 路徑缺 Python IDS_ROI 裁切→Hough 可能抓錯圓 | **Adopt** | 加 config ROI（預設 off；appsettings live=240,0,700,680） |
| 3 | High | **既有單軸** handler 同樣 fail-open-on-Skip（Skip→放行） | **Flag（待使用者決定）** | 未改（既有 blackhat 路徑，出本案 scope） |
| 4 | Medium | 配對投票 tie-break 與 Python 分歧 | **Adopt** | `MoldCodePairVoter`：改 max-score + 首見序 |

## 6. Final Review Gate
- **Q1 fail-closed**：pass — 辨識失敗/無物件/非有限信心/低信心 NG 一律不送 Result(OK)（`VerifyMoldCodePairCycleCommandHandler` IO 映射 + `MoldCodePairCycleHandlerTests` 斷言）
- **Q2 doc-vs-code**：pass — 本文件每條對應實際 diff + 測試；golden 數字由實跑產出
- **Hostile review by**：Codex（GPT-5.4，實際回覆 4 findings，已逐條驗證 file:line + triage）

## 7. 待辦（未完）
- **Finding 3**：既有單軸 `VerifyMoldCodeCycleCommandHandler` fail-open-on-Skip —— 待使用者決定是否一併修。
- **P4 實機**：UI 工單頁接雙軸 cycle、live IDS 相機（ROI 已備）、PLC 氣吹點位、GPU OnnxRuntime；live 路徑需實機驗 ROI/曝光。
- **反哺**（P5）：warpPolar+annulus 前處理 / 雙軸三態框架回流共用平台。

## Related
- charter：`PROJECT-CHARTER.md`（P4 設備整合）
- 前處理規格（來源）：`G:\隱眼專案\前處理與優化策略.md`、`V6.7.1_說明.md`
- 規則：`decision-lifecycle.md`（supersede blackhat）、`fail-mode-output.md`（Q1）、`review-triage-and-threshold.md`
