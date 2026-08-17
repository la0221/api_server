# 模號 OCR 核對分料 — 專案目的與開發架構（Charter）

> **狀態**：draft（文件先行，未動 code）
> **建立**：2026-06-04
> **宿主專案**：`ai_vision`（AIVision，Clean Architecture .NET 8）— 決策 A：以 AIVision 當 app 殼
> **定位**：用公司**既有工具**組裝一條隱形眼鏡模仁「模號 OCR 核對 → 混料氣吹」產線辨識,成果**反哺**回共用平台

---

## 1. 目的（為什麼做這個 Project）

隱形眼鏡乾片模仁上有刻印 **模號（M101）+ 兩位模穴號（01~18，缺 11）**。產線要在零件流經時：

1. **讀出模號/穴號**（OCR 辨識，封閉 18 類，不是通用 OCR）
2. **核對操作員預期值**（防混料）
3. **混料 → 控 IO 氣吹剔除**

**雙重核心目的：**
- **(主) 落地**：用既有工具拼出這條辨識線，準確率最高、且 **150ms 內出辨識結果**。
- **(副) 反哺**：過程中淬煉出的通用能力（三態核對框架、凹刻字前處理、本地 ONNX 推論範式、重訓資料管線）**回流到共用平台**（OpenCV_Vision / yolo_service / VisionFlow / AIVision），不是只做一個一次性站。

### 範圍邊界
| 做 | 不做 |
|---|---|
| 取像 → 前處理 → OCR 辨識 → 三態決策 → 控 IO 氣吹（本地 150ms 熱迴圈） | 缺陷檢測（刮傷等，那是 AINAVI/VisionFlow 的事） |
| 模號/穴號封閉集辨識 + 重新訓練模型 | 通用文字 OCR |
| 用現有 M101 照片驗證 | 改 VisionFlow 邊界 / 進 150ms 熱路徑 |

---

## 2. 既有工具重用對照（不重造）

| 需要的能力 | 既有工具 | 來源 | 動作 |
|---|---|---|---|
| App 殼 / 檢測週期編排 | AIVision Clean Architecture + `StartInspectionCycleCommand` | `ai_vision` | 宿主 |
| IDS 相機取像 | `ICameraPort` + `IdsCameraPort` + `IdsPeakLibrary` | AIVision | 重用 |
| TCP→PLC IO（氣吹） | `IPlcPort` + `ModbusTcpPlcAdapter` + `FakePlcPort` | AIVision | 重用 |
| AI 推論接縫 | `IAiInferencePort` | AIVision | 擴充/沿用 |
| 光源控制（斜側光提對比） | `ILightPort` | AIVision | 重用 |
| 三態核對決策（純函式、194 測試過） | `MarkingVerifier` / `MarkingObservation` / `MarkingDecision` | OpenCV_Vision.Core.MarkingVerify | 參考/移植 |
| 辨識器抽換 port | `IMarkingRecognizer` | OpenCV_Vision.Core | 沿用介面 |
| 數字定位 | `MarkingDigitLocator` | OpenCV_Vision.Core | 重用 + 升級（見 §6） |
| 凹刻字前處理（blackhat，已驗證拉開 1/7） | `engrave_enhance()` | `VISION/Yolo/_1v7_diagnose/preprocess.py` | 移植成 OpenCvSharp |
| YOLO 訓練/推論/ONNX 匯出 | yolo-service（FastAPI + Ultralytics） | `yolo_service` | 重新訓練 + 匯 ONNX |
| 離線驗證 harness（混淆矩陣） | `moldcode-ocr` CLI | OpenCV_Vision.CLI | 重用 |

> **只缺一塊要新建**：本地 ONNX 推論（全專案無 OnnxRuntime 依賴）→ 新增 `Microsoft.ML.OnnxRuntime.Gpu`（吃 4090）。

---

## 3. 開發架構（AIVision Clean Architecture 內落點）

對齊 AIVision「每服務 Clean Architecture + Ports & Adapters」原則。新元件標 ★。

```
AIVision.Domain
└─ MoldCode/ ★
     MoldCode(值物件: M101 + 01..18) / 封閉字集定義 / 三態列舉(或引用 OpenCV_Vision)

AIVision.Application
├─ Ports/Devices: ICameraPort✓  IPlcPort✓  ILightPort✓  IAiInferencePort✓   (既有)
├─ Ports/MoldCode: IMoldCodeRecognizerPort ★   (= 前處理+ONNX 辨識, 回 MarkingObservation)
└─ MoldCode/UseCases: VerifyMoldCodeCycleCommand(+Handler) ★
       grab → recognize(多幀投票) → MarkingVerifier.Decide(三態) → IPlcPort 氣吹

AIVision.Infrastructure
├─ Devices/Camera/Ids ✓   Devices/Plc ✓   (既有)
└─ MoldCode/ ★
     OnnxMoldCodeRecognizer : IMoldCodeRecognizerPort
        OpenCvSharp 前處理(定位圓→相對帶→blackhat) + OnnxRuntime.Gpu YOLO 推論
     MultiFrameVoter (自適應: 票數/時間雙條件早停)
```

### 150ms 本地熱迴圈（已實測:核心 ~8ms,150ms 寬裕）

```
PLC/相機 trigger
  → ICameraPort.Grab (IDS)                     ~?ms  ← 最大未知,待實機量(剩~140ms 都給它)
  → IMoldCodeRecognizerPort.Recognize
        OpenCV 前處理(定位+blackhat)            ~3ms  (實測)
        ONNX YOLO 推論(4090)                    ~5ms  (實測)
  → MultiFrameVoter(時間夠就多投幾張)
  → MarkingVerifier.Decide(三態, T_alarm@config) <1ms
  → IPlcPort 寫 IO 點(混料 MixedAlarm → 氣吹)   TCP→PLC
  → IResultSink: 存圖+log,(非同步)推 VisionFlow 做 UI/監控  ← 不在熱路徑
```

**VisionFlow 角色**:迴圈外的編排/記錄/UI（公版,非同步圍繞）,**不卡 150ms**。

### 三層三態語意對齊（fail-closed 一路到底）
`MarkingVerifier`(Match/TrustInput/MixedAlarm/Skip) ↔ 設備放行/氣吹 ↔ (若上報)VisionFlow `Completed/InfrastructureFailed/Rejected`。失敗一律不可當「合法通過」。

---

## 4. 辨識方案（準確率最高的那條 stack）

① 重訓 YOLO 當辨識器(基準 97.6%) → ② 多幀投票(→99%+) → ③ 目標圓隔離(殺鄰模) → ④ 定位改相對圓(修固定 y 帶 bug) → ⑤ blackhat 前處理(治標 1/7) → ⑥ 封閉集約束(M101/01..18,出集→no-result) → ⑦ T_alarm 校準(appsettings) → ⑧ blackhat+增強重訓(治本)。

> ⚠️ **train/infer 對齊**:blackhat 若進模型,訓練與推論必須用同一套前處理參數。

---

## 5. 模型策略（重新訓練 + 可擴充）

**重新訓練（要做）:**
- 資料:現有 M101 照片(§7)+ blackhat 增強 + 旋轉/倒置增強 + **加重形近字樣本**(1/7、4/1、4/2、13/15、15/03)。
- 匯出:ONNX `opset=17, simplify=True`(yolo_service 既有規則),供本地 OnnxRuntime。
- 對齊:訓練前處理 == 推論前處理。

**可擴充設計（要做）:**
| 擴充軸 | 怎麼擴 |
|---|---|
| 更多模號(M102…) / 更多穴號 | `classSet` 走 config,不寫死;模型類別表外掛 |
| 辨識器抽換 | `IMoldCodeRecognizerPort` / `IMarkingRecognizer`(本地 ONNX / 遠端 TCP / classical NCC 可換) |
| 模型版本 | 沿用 yolo-service model registry + ONNX 版本切換 |
| 多站/多料號 | config 對應表(workflowKey 模式) |
| 前處理管線 | 沿用 OpenCV_Vision recipe / PreProcess 節點 |

---

## 6. 定位器升級（沿用 + 修 bug）

重用 `MarkingDigitLocator`,但修 handoff 點名的問題:
- 現用**固定畫面 y 帶 0.78–0.94** → 圓漂移/旋轉時失效、會抓入侵鄰模 → 改 **相對偵測圓**(先選最置中最完整圓 + 遮罩,再相對該圓定位數字)。
- 前處理 CLAHE → 加 **blackhat** 強化(安全:定位用,不影響模型)。

---

## 7. 驗證計畫（用現有照片）

- **資料集**:`VISION/OpenCV_Vision/Project/6_4模號M101測試_含信心度log/M101/`(01..18 子夾,檔名/夾名=ground truth)。
- **harness**:重用 `moldcode-ocr` CLI(混淆矩陣 + `--dump`)。
- **指標**:每類 precision/recall、整體 accuracy、**形近字對錯誤率**、**混料漏報/誤報(最關鍵)**、conf 分布 vs 正確性。
- **目標**:辨識追平→超越 YOLO baseline 97.6%;多幀投票後整顆 99%+;**漏報(真混料當相符放行)趨近 0**。
- **基準對照**:classical NCC = 6.4%(死牆,已證);重訓 ONNX 要顯著勝出。

---

## 8. 反哺計畫（回流共用平台）

| 產出 | 反哺給 | 內容 |
|---|---|---|
| 三態核對框架 `MarkingVerify` | OpenCV_Vision.Core / 升級共用 | 封閉集標記核對通用化(AIVision 也能用) |
| blackhat 凹刻字前處理 | OpenCV_Vision PreProcess(`IVisionAlgorithm` 節點) | `BlackHatEngraveAlgorithm` |
| 本地 ONNX 推論範式 | 共用 / VisionFlow `IImageProcessor` | `OnnxRecognizer` 樣板 |
| M101 資料集 + 重訓配方 | yolo_service | dataset + 增強/超參 |
| IDS/PLC adapter 精修 | AIVision / 共用 | 設備 adapter 強化 |

---

## 9. 階段與待辦

| Phase | 內容 | 狀態 |
|---|---|---|
| **P0 文件** | 本 charter（目的+架構+驗證+模型） | ✅ 完成 |
| **P1 辨識核心** | `OnnxMoldCodeRecognizer` + 多幀投票 + `MarkingVerifier` + 週期 + harness | ✅ **C# 99.26% / 20-32ms CPU**(見 [PHASE-1-2-RESULT](PHASE-1-2-RESULT.md)) |
| **P2 重訓模型** | v1 全圖 **98.84%** → v2 裁切+遮罩+強化增強 **100%**(leaky);⚠️ 真泛化需更多實體零件樣本 | ✅ v1/v2 完成 |
| **P3 定位升級** | 相對圓裁切+遮罩(圓偵測 100%,殺鄰模,修固定 y 帶 bug)+ C# 港 locator | ✅ 完成(步驟見 [pipeline-steps](pipeline-steps/README.md)) |
| **P4 設備整合** | 接 IDS(ICameraPort)+ TCP→PLC 氣吹 + DI 註冊 + GPU OnnxRuntime | ⏳ 待實機 |
| **P5 反哺** | 把 §8 通用件回推各共用專案 + 寫 ADR | ⏳ 待 P3/P4 |

---

## 10. 開放決策 / 待確認

1. `MarkingVerifier` 是**跨專案參考** OpenCV_Vision.Core,還是**升級成共用庫**再被兩邊引用?(反哺方向)
2. IDS 實機**取像時間**(定「150ms 內投幾幀」)。
3. PLC IO **氣吹點位**定義 + TCP/Modbus 細節。
4. `T_alarm` 確切值(log 框在 0.71~0.95,預設 0.85,現場校)。
5. 是否在 `project-registry.json` 給此能力獨立登記,或視為 `ai_vision` 內的 feature。

---

## Related
- OpenCV_Vision mold-code 設計:`VISION/OpenCV_Vision/docs/dev/mold-code-verify/design.md`
- 1/7 前處理驗證:`VISION/Yolo/_1v7_diagnose/`
- VisionFlow 邊界:`VISION/VisionFlow/docs/spec/visionflow-boundary-and-executor-spec.md`
- AIVision 架構:`VISION/AIVision/TARGET_ARCHITECTURE.md`
