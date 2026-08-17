# P1+P2 成果 + 架構對照（2026-06-04 自主開發）

> 對照基準:[PROJECT-CHARTER.md](PROJECT-CHARTER.md)。每 phase 完成對照架構(user 要求)。

## 結果摘要(可以辨識 ✅)

| 項目 | 數字 |
|---|---|
| Python 重訓 val 準確率(held-out,blackhat+旋轉增強) | **98.84%** |
| C# OnnxMoldCodeRecognizer 全 271 張 | **99.26%**(269/271) |
| 對照:classical NCC / YOLO baseline | 6.4% / 97.6% |
| C# 端到端單張(前處理+ONNX,**CPU** OnnxRuntime) | p50 **20ms** / p95 **32ms** |
| GPU 推論(先前 bench,4090) | ~5ms → 端到端 ~8ms |
| 檢測週期(3 幀投票+三態+IO,CPU) | 61–72ms |
| 殘留誤判 | 07→16、15→12(7 與形近字;投票+補樣本可消) |

⚠️ **誠實聲明:** 訓練資料每類僅「單一實體零件的連拍 burst」→ train/val 同 burst 有洩漏,val 準確率偏樂觀。**證明管線可學 + blackhat 有效 + 全鏈打通**,但**產線模型需更多實體零件樣本重訓**(P2 待真資料)。

## 架構對照(charter §3 → 實作落點)

| charter 設計 | 實作 | 落點 | 對照 |
|---|---|---|---|
| Domain 三態 + 觀測 | MarkingObservation/Decision/Outcome/Verifier | `AIVision.Domain/MoldCode/` | ✅ 移植自 OpenCV_Vision(194 測試邏輯) |
| 多幀投票 | MultiFrameVoter(自適應早停) | `AIVision.Domain/MoldCode/` | ✅ 純函式 |
| 辨識器 port | IMoldCodeRecognizerPort | `AIVision.Application/Ports/MoldCode/` | ✅ |
| 檢測週期 | VerifyMoldCodeCycleCommand(+Handler) | `AIVision.Application/MoldCode/` | ✅ MediatR,沿 StartInspectionCycle 模式 |
| 本地 ONNX 辨識器 | OnnxMoldCodeRecognizer + 前處理 | `AIVision.MoldCode.Onnx/`(新專案) | ⚠️ 見偏差 |
| blackhat 前處理 | MoldCodePreprocessor(OpenCvSharp) | `AIVision.MoldCode.Onnx/` | ✅ 與 Python 1:1 對齊 |
| 相機/IO | ICameraPort / IPlcPort(+IoCommand.Blow) | AIVision 既有 | ✅ 重用,氣吹點位加 IoCommand.AirBlow |
| 離線驗證 | Harness(混淆矩陣+cycle demo) | `AIVision.MoldCode.Harness/` | ✅ FakePlc + 假相機 |

### 與 charter 的偏差(1 項,且更好)
charter §3 原寫 OnnxMoldCodeRecognizer 放 `AIVision.Infrastructure`。**實作改放獨立專案 `AIVision.MoldCode.Onnx`**,原因:本機 Infrastructure 因缺相機 DLL(IDS/Hikvision 不在 User-PC 子集)**無法建置**。獨立專案只依賴 Domain+Application+OnnxRuntime+OpenCvSharp → 本機可建可驗,**且更符合 charter「辨識器可抽換、DL 不綁 Infra」原則**。產線機(有相機 DLL)可由 Infrastructure/DI 參照此專案,或維持獨立。

## 硬規則符合
- ✅ 無 hardcode IP/Port/門檻 → MoldCodeCycleOptions / MoldCodeOnnxOptions 走設定
- ✅ async/await + CancellationToken(IPlcPort/ICameraPort 呼叫)
- ✅ fail-closed:辨識失敗回 Failed,不回「看似合法」碼
- ✅ ONNX opset=17 simplify

## 產物
- 程式:`AIVision.Domain/MoldCode/`、`AIVision.Application/{Ports/MoldCode,MoldCode}/`、`AIVision.MoldCode.Onnx/`、`AIVision.MoldCode.Harness/`
- 模型:`AIVision.MoldCode.Onnx/models/moldcode_bh_v1.onnx`(+names+train-report)
- 訓練:`VISION/Yolo/_moldcode_train/`(train_moldcode.py + data_blackhat + runs + report.json)
- build:Domain/Application/Onnx/Harness 全綠;Infrastructure 因缺相機 DLL 紅(與本案無關)

## 下一步(P2 真資料 / P3 定位 / P4 設備)
1. **P2 真資料重訓**:多實體零件樣本(消 leakage)+ 補 07/形近字 → 真實泛化準確率。
2. **P3 定位升級**:相對偵測圓 + 遮罩 + blackhat 定位(目前是「全圖分類」,未用 locator;鄰模干擾尚未處理)。
3. **P4 設備整合**:接真 IDS(ICameraPort)+ TCP→PLC(IPlcPort)+ DI 註冊 OnnxMoldCodeRecognizer。
4. **GPU**:Microsoft.ML.OnnxRuntime → .Gpu(吃 4090,推論 ~5ms)。
