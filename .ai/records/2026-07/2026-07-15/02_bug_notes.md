---
date: 2026-07-15
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）— API server 中央推論
tags: [前處理, ROI, 模型版本, 漂移, ONNX, Passes, 效能]
status: draft
promote_to_pitfall: true
---

# Bug Notes - 2026-07-15

## 坑 1：把 WPF 的前處理參數整組複製到 server 會裁錯圖 —— `Roi*` 不可照抄

### 1. 情境

API 中央推論要「前處理與訓練對齊」，直覺是把 WPF `appsettings.json` 的 `MoldCodeWarpPolar.Preprocess` 整組複製過去。

### 2. 問題現象（在複製前先攔截到，未實際踩）

WPF 的 ROI 是 `RoiX=240, RoiY=0, RoiW=700, RoiH=680`，但 API 契約收的測試/edge 圖是 **600×580**（已裁的判定區域）。ROI 700×680@x=240 對 600×580 根本超界；`CropRoi` 雖會 `Math.Clamp` 不致崩，但會**裁出錯誤區域** → Hough 找不到正確的圓 → fail-closed 或讀錯。

### 3. 最終原因

`Roi*` 的用途是「**送 Hough 前先裁掉背景**」，對齊 Python 的 `crop_roi(IDS_ROI)`——那是**全幅相機圖（1280×1024）** 的座標。程式碼註解寫得很清楚：

- `WarpPolarParams.RoiW`：「`<=0` → 不裁（用整張；**離線已裁圖 / golden 用此預設**）」
- `WarpPolarPreprocessor.CropRoi`：「對齊 Python engine.predict 的 crop_roi(IDS_ROI)；**離線已裁圖請保持 ROI=0**」

WPF 走**實機全幅相機**路徑 → 需要 ROI。API 契約走「**edge 已前處理的判定區域圖**」→ 必須 ROI=0。

### 4. 最終解法

API `appsettings.json` 的 `MoldCodeWarpPolar.Preprocess` **只複製與訓練對齊的參數**（`RInner/Imgsz/PadValue/Hough*`），**刻意不設 `Roi*`（=0=不裁）**，並在 json 加註解說明「這是刻意不同，非疏漏」。實測 180/180 讀值正確，證明判讀正確。

### 5. 下次遇到類似問題，AI 應先檢查

- 跨 host 複製前處理設定時，**先分辨每個參數屬「與訓練對齊」還是「與取像來源對齊」**：
  - 與**訓練**對齊（RInner/Imgsz/PadValue/Hough 門檻）→ **必須照抄**。
  - 與**取像來源/畫面座標**對齊（ROI、相機解析度）→ **依該 host 的輸入決定，不可照抄**。
- 判斷輸入圖是「全幅」還是「已裁」：比對**影像實際尺寸 vs ROI 範圍**，超界就是已裁圖。
- 程式碼註解常已寫明（本案 `RoiW<=0 → 不裁 / 離線已裁圖請保持 ROI=0`）——**先讀註解再抄設定**。

### 6. 是否應升級成避坑指南？

- [x] 已驗證　[x] 容易重複踩坑　[x] 未來應排除　[x] 對開發決策有約束價值

結論：yes（「前處理設定跨 host 複製時，要分『對齊訓練』vs『對齊取像來源』」是通用陷阱，未來 edge/server/離線三路並存只會更常遇到）。

---

## 坑 2：模型版本漂移屬實 —— 兩份都叫 V6.7.1 但檔案不同

### 1. 情境

要幫 API 配「和 WPF 一樣的已知良好模型」，發現有兩個來源：WPF 用的 `D:\AIVisionModels\v671\`，以及倉庫 `D:\AIVisionModels\pairs\v6.7.1\`。

### 2. 現象

md5 比對**兩者不同**：

| 檔 | `v671\`（WPF 實際在用） | `pairs\v6.7.1\`（倉庫） |
|---|---|---|
| mohao.onnx | `d42bb1b7ec83884a7eaabc711f9d1cba` | `515a827122a9900b3d378ee717f034a5` |
| xuehao.onnx | `5d80f6900ba8ed2b6ba0d8a69550f4a2` | `a1f1c4f5363d60a1910619440c4a240d` |

兩份都自稱 V6.7.1，**無法從檔名/路徑判斷誰是正版**。

### 3. 原因

模型散落各處、靠人工複製，沒有集中登錄與雜湊/報告佐證 → 正是設計書 `2026-07-12_api_server_deployment.md` §2.6 指出的漂移問題（該文早已點名「現況『兩份 V6.7.1 mohao』正是漂移案例」）。另 `pairs\` 雖已是 `<版本>\{mohao,xuehao}.onnx` 結構，但**缺 `.names.json` / `.report.json`**，三件套不完整 → 更難佐證。

### 4. 目前處置（非最終解）

API 先指向 **`v671\`**（WPF 實際在用者），確保讀值可對照「已知良好」基準；**不逕自選 pairs 版**，避免引入未知變因。

### 5. 下次遇到類似問題，AI 應先檢查

- 有多個同名版本時，**先 md5/雜湊比對**再選，不要憑路徑或日期猜。
- 選「目前 production 實際在用的那份」當基準，才能把「API 路徑對不對」與「模型好不好」兩個變因分開。
- 真正的解是模型中樞（集中登錄 + 版本雜湊 + report），而非人工挑檔。

### 6. 是否應升級成避坑指南？

- [x] 已驗證　[x] 容易重複踩坑　[x] 未來應排除　[x] 對決策有約束價值

結論：yes（且直接佐證「模型中樞（P3）」的必要性——這不是紙上規劃，是現況已在漂移）。

---

## 坑 3：⚠️ 用 Debug build 量效能 → 數字慢一倍，差點誤判架構決策

### 1. 情境

要量 P0 可行性數字（server 端單張推論延遲 `elapsedMs`），對照使用者拍板的產線節拍 **<400ms**。

### 2. 錯誤現象

用 `dotnet build -c Debug` 起 server 量到：**Passes=2 → 747ms/張、Passes=1 → 387ms**。
對照 <400ms 節拍，據此得出結論：**「CPU 完全出局，GPU 是必須」** —— 並開始往「裝 CUDA + 切分 ORT 套件」的方向走。

### 3. 轉折：發現與 edge 設定的量級矛盾

檢查 edge 的 `MoldCodePairCycleOptions` 發現：`MaxFrames=7`、**`TimeBudgetMs=120`**、`MinConsensusVotes=3`。
即 edge 設計期望「**120ms 內跑完 7 幀**」→ 隱含每幀 **~20-40ms**。而實測 747ms **差了 20-40 倍**。

> **這種量級落差通常代表量測環境有問題，不是設計錯。** → 回頭質疑自己的量測基準。

### 4. 最終原因

**Debug build**。改 `-c Release` 後，**同一組設定、同一批圖**：

| Build | Passes | 平均 | p90 |
|---|---|---|---|
| Debug | 2 | 747ms | 889ms |
| **Release** | 2 | **385ms** | 409ms |
| Debug | 1 | 387ms | 452ms |
| **Release** | **1** | **191ms** | **209ms** |

**Release 快約 2×**。原因：ONNX Runtime native 本身是預編最佳化的，但**前後處理的 managed/OpenCvSharp 膠合層**（張量搬運、像素轉換）在 Debug 下未最佳化 + 無 inlining，代價極高。

### 5. 結論反轉

Release + Passes=1 = **191ms（p90 209ms）** → 對 <400ms 節拍**可行**，**CPU 沒有出局**，GPU 從「必須」降級為「可選優化」。
**若沒回頭質疑量測基準，就會白做一輪 CUDA 安裝 + ORT 套件切分的重工，還會給出錯誤的架構建議。**

### 6. 下次遇到類似問題，AI 應先檢查

- **效能量測一律用 `-c Release`**。Debug 數字**不可**用來做任何架構/可行性判斷。
- 拿到數字先問「**這和系統既有設定/設計假設是否同一量級**？」。本案 edge 的 `TimeBudgetMs=120` 就是現成的對照基準 —— **量級差 20 倍時，先懷疑量測，別急著下架構結論**。
- 量測前先列清干擾變因：**build 組態**、暖機（首張 1141ms）、Debugger 附加、能源模式、背景負載。
- 下重大結論（「X 出局」「必須換硬體」）前，**先排除便宜的變因**，再談貴的方案（裝 CUDA/換硬體/重構）。

### 7. 是否應升級成避坑指南？

- [x] 已驗證　[x] 極容易重複踩坑　[x] 未來應排除　[x] **對開發決策有高度約束價值**

結論：**yes（最高優先）**。「Debug 量效能」是最常見也最貴的量測錯誤——它不只給錯數字，還會導向錯誤的架構決策與無謂重工。
