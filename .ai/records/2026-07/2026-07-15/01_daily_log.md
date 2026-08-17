---
date: 2026-07-15
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— API server 中央推論 P0 量測
tags: [AIVision, API, 中央推論, ONNX, 延遲, P0, 可行性, Passes, 版本漂移]
status: draft
---

# Daily Log - 2026-07-15

## 1. 今日主題

接昨日交接第一項：**把 `MoldCodeWarpPolar` 配上實體模型 → 驗讀值 → 量 `elapsedMs`**（部署書 P0 可行性數字）。使用者拍板**節拍 < 400ms**。結果：**讀值 180/180 正確**，且 **Release + Passes=1 = 191ms（p90 209ms）→ CPU 中央推論可行**（但餘裕薄、多線吞吐仍是限制）。

> ⚠️ **本日重大自我修正**：初期用 **Debug build** 量到 747ms，一度judged「CPU 出局、GPU 必須」。改 **Release** 後同設定僅 385ms（快一倍），Passes=1 更降到 191ms。**Debug build 是無效的效能量測基準** —— 見 `02_bug_notes.md` 坑 3。

## 2. 今日完成事項

- **配真模型**：API `appsettings.json` 的 `MoldCodeWarpPolar` 指向 `D:\AIVisionModels\v671\{mohao,xuehao}.onnx`（＝WPF 實際在用的那組，讀值可對照已知良好基準）。
- **釐清前處理位置（重要）**：複製 WPF 的 `Preprocess`（RInner 0.6 / Imgsz 640 / PadValue 255 / Hough 全套）**但刻意不設 `Roi*`（=0=不裁）**。依據 `WarpPolarPreprocessor.CropRoi` 註解「**離線已裁圖請保持 ROI=0**」——WPF 那組 ROI(240,0,700,680) 是給**全幅相機圖**用的；本端點契約收「edge 已前處理的判定區域圖」，再套相機 ROI 會裁錯。
- **驗讀值（180 張，M101 全 18 穴號 × 10）**：正解取自資料集目錄結構。**Passes=1：180/180（100%）；Passes=2：179/180（99.4%）**。信心多在 0.95～1.0。
- **量 P0 延遲（server 端純推論 `elapsedMs`，已暖機，CPU-only / Ryzen 7 4800H）**：
  | Build | Passes | 每張 ONNX 次數 | 雙軸正確 | 平均 | p90 |
  |---|---|---|---|---|---|
  | Debug | 2 | 4（2 pass × 2 head） | 179/180 | 747ms | 889ms |
  | Debug | 1 | 2 | 180/180 | 387ms | 452ms |
  | **Release** | 2 | 4 | 60/60 | **385ms** | 409ms |
  | **Release** | **1** | 2 | **180/180 (100%)** | **191ms** | **209ms**（p99 228ms） |
  - **Release vs Debug ≈ 2×；Passes 2→1 ≈ 2×；合計 4×**（747 → 191ms）。
  - 含 localhost HTTP 來回 `wallMs`：平均 269ms、p90 289ms（尚未含真實網路/TLS）。
  - 冷啟動首張 1141ms（含 ONNX session 暖機），統計已排除。
- **可行性結論（對 <400ms 節拍）**：Release+Passes=1 的 191ms（wall 269ms）**可行**，但 400ms 節拍扣掉 wall 289ms(p90) 僅剩 ~110ms 給取像/PLC/決策 → **餘裕薄**。多線共用時 CPU 吞吐（單線約 5 次/秒）才是真正瓶頸。
- **發現本機有閒置 GPU**：NVIDIA RTX 3050 Laptop 4GB；驅動支援 CUDA 12.7，但 **CUDA Toolkit / cuDNN 未安裝** → 本機要量 GPU 需先裝（~3GB）。
- **確認版本漂移屬實**（交接文件早有疑慮）：`v671/mohao.onnx` 與 `pairs/v6.7.1/mohao.onnx` **md5 不同**，兩份都自稱 V6.7.1（xuehao 亦然）。→ 正是設計書 §2.6 模型中樞要解的問題。
- **發現模型倉庫已存在**：`D:\AIVisionModels\pairs\{v6.7, v6.7.1, v6.7.2}\{mohao,xuehao}.onnx`，但**缺 names/report**（設計書講的「三件套」目前只有 onnx）。

## 3. 今日重要決策 / 判讀

- **API 的 `Roi*` 一律 0**：契約收前處理圖，server 不再套相機 ROI（與 WPF 全幅路徑刻意不同，非疏漏）。
- **暫不改 `Passes` 預設**：雖然 180 張顯示 Passes=1 快一倍且未見退步，但樣本僅單一模號(M101)、單一 session、且**可能屬訓練期同分布資料**，不足以推翻 production 的 Passes=2。→ 列為待驗證槓桿，不逕自改。
- **GPU 不是純加套件**：`AIVision.MoldCode.Onnx` 被 **WPF(edge) 與 API(server) 共用**，直接把 ORT 換 GPU 版會波及 edge。要嘛條件式套件參照、要嘛拆專案 → 需設計，不是一行改。

## 4. 今日改動摘要（AIVision）

- Api：`appsettings.json` — `MoldCodeWarpPolar` 配 v671 實體模型 + 完整 `Preprocess`（ROI 刻意留 0，並加註解說明原因）。`Passes` 維持 2（量測後已還原）。
- 無 C# 程式碼改動（今日為配置 + 量測）。

## 5. 尚未完成 / 明日接續

- **⚠ `Passes=1` 大樣本驗證（最高優先）**：可行性完全建立在 Passes=1 的 191ms 上，但目前僅驗過 **單一模號(M101)、單一 session(2026-06-05)、可能屬訓練同分布** 的 180 張。需跨模號/多 session/含難例驗證，確認砍掉接縫修正 pass 不會在難例上退步。**驗過才可把 API 預設改成 1**（目前保守維持 2）。
- **⚠ edge `TimeBudgetMs=120` 與實測矛盾**：edge 設定期望「120ms 內跑到 7 幀」（隱含每幀 ~20-40ms），但實測單幀最快 191ms → **edge 的多幀投票實際上永遠只跑得完 1 幀就超預算**。需確認 edge 現場真實表現，這牽涉三態決策的投票基礎是否成立。
- **多線吞吐評估**：單線 CPU 約 5 次推論/秒。幾條線共用一台 server？→ 決定是否仍需 GPU/多副本。
- **GPU（可選，非必須了）**：本機有 RTX 3050 但缺 CUDA Toolkit/cuDNN（需裝 ~3GB）。且 `AIVision.MoldCode.Onnx` 被 **WPF(edge) 與 API(server) 共用**，換 GPU 版 ORT 會波及 edge → 需套件切分（lib 只依 `Microsoft.ML.OnnxRuntime.Managed`，各 host 自選 native）。**注意：換套件不夠，還要在建 InferenceSession 時明確 append CUDA EP。**
- 模型版本漂移收斂：決定 `v671` vs `pairs/v6.7.1` 哪個是正版；補齊 names/report 三件套。
- 其餘照舊：模型倉庫/發佈 API（P3）、TLS/authn、持久化、edge 降級選擇器（P2）。

## 6. 今日一句話總結

中央推論配真模型後**讀值 180/180 正確**；**Release + Passes=1 = 191ms（p90 209ms、wall 269ms）→ 對 <400ms 節拍可行**（餘裕薄、多線吞吐待評）。過程中最大教訓：**初期用 Debug build 量到 747ms 差點誤判「CPU 出局」，Release 後快一倍** —— 效能量測絕不可用 Debug。
