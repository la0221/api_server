# 處理步驟記錄 — 從原圖到辨識結果

> 用單一範例(`M101/07`,含難辨的「7」)逐步展示**每個處理動作與順序**,最後得到結果。
> 一覽圖:[STEPS_montage.png](STEPS_montage.png)。範例最終辨識 = **M101/07 (conf 1.00)** ✅

## A. 影像處理 pipeline(每張圖按此順序跑)

| # | 步驟 | 圖 | 做什麼 / 為什麼 | 對應 C# 程式 |
|---|------|----|----------------|-------------|
| 00 | 原圖(灰階) | [00](00_raw_grayscale.png) | IDS 取像的 ROI(600×580),刻字淺、對比低 | `ImageData` |
| 01 | **圓偵測** | [01](01_circle_located.png) | HoughCircles 找鏡片圓,選最置中(綠=鏡片緣,橘=1.5×裁切界) | `MoldCodePreprocessor.LocateCircle` |
| 02 | **裁切 + 遮罩** | [02](02_cropped_masked.png) | 以圓裁切 zoom 進零件 + 圓外遮黑 → **殺鄰模/背景**(修固定 y 帶 bug) | `MoldCodePreprocessor.CropToPart` |
| 03 | CLAHE | [03](03_clahe.png) | 局部對比增強,淺刻字先拉出來 | `EngraveEnhance`(內) |
| 04 | **blackhat** | [04](04_blackhat.png) | 形態學凸顯凹刻暗字 → 字變白底浮出(治標 1/7) | `EngraveEnhance`(內) |
| 05 | normalize | [05](05_normalized.png) | 拉滿動態範圍 | `EngraveEnhance`(內) |
| 06 | 去雜訊(final) | [06](06_denoised_final.png) | open + median 去背景紋路 = 最終增強 | `EngraveEnhance`(內) |
| 07 | 縮放 320 | [07](07_model_input_320.png) | resize → 模型輸入張量(/255,灰階複製三通道) | `MoldCodePreprocessor.ToTensor` |
| → | **ONNX 推論** | — | yolo11n-cls(重訓 v2)→ softmax → argmax → `M101/穴號` + 信心 | `OnnxMoldCodeRecognizer.Recognize` |
| → | **多幀投票** | — | 連拍多幀碼多數決(自適應早停) | `MultiFrameVoter.Vote` |
| → | **三態決策** | — | Match / TrustInput / **MixedAlarm**(門檻 0.85) | `MarkingVerifier.Decide` |
| → | **控 IO** | — | MixedAlarm → `IoCommand.Blow()` 氣吹剔除;否則放行 | `VerifyMoldCodeCycleCommandHandler` |

> ⚠️ **train/infer 對齊**:00→07 這套前處理在**訓練(Python)與推論(C#)必須一字不差**,否則模型沒看過。Python 端對應 `VISION/Yolo/_moldcode_train/train_moldcode_v2.py`。

## B. 開發步驟(怎麼一路做到結果的順序)

1. **看圖診斷 1/7** — 真圖比對,找出根因:淺刻低對比(非字形無法分)。→ `VISION/Yolo/_1v7_diagnose/`
2. **驗證 blackhat** — 證明 blackhat 形態學能把 1/7 拉開。→ 同上 `preprocess.py`
3. **選型確認** — OpenCV+YOLO(非 Paddle);本地 ONNX(非遠端,因 150ms 硬限)。
4. **速度實測** — 核心 ~8ms(4090),150ms 寬裕。→ `VISION/Yolo/_speed_bench/`
5. **寫 charter** — 目的+架構+驗證+模型策略。→ `../PROJECT-CHARTER.md`
6. **P1 辨識核心** — Domain/Application/Onnx + harness,占位模型跑通三態+投票+氣吹。
7. **P2 重訓 v1** — blackhat 全圖 + 旋轉增強 → ONNX,val 98.84%。
8. **P3 定位 + P2 v2** — 相對圓裁切+遮罩(殺鄰模)+ 強化擬真增強重訓 → ONNX v2(本檔範例用 v2)。
9. **C# 港 locator** — 推論端對齊 v2 裁切流程,harness 重驗。

## C. 誠實聲明
驗證資料每類僅單一實體零件連拍 → train/val 同 burst 有洩漏,準確率偏樂觀。**證明管線正確、blackhat/裁切有效**,但**產線模型需更多實體零件樣本重訓**才知真實泛化。

## D. 怎麼重生這些圖
```
py -3.10 VISION/Yolo/_moldcode_train/generate_steps.py   # 改 HERO 換範例
```
