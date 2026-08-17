# CRNN 引擎接入調查（OCR_demo → AIVision 中央線）

> 2026-07-31 調查（來源：`D:\OCR_demo\models\crnn\HANDOFF.md`、`app\ocr\crnn_engine.py`、`app\serve.py`）。
> 狀態：**調查完成、路線待拍板**。

## 1. CRNN 是什麼（與現行 ocr_pair 的本質差異）

| | 現行 ocr_pair（雙 head cls） | CRNN（正名：detector + Non-AR） |
|---|---|---|
| 模型 | 兩顆 yolov8s-cls（mohao 20 類／xuehao 18-19 類） | **兩顆**：YOLOv8n detector（找字框，6.3MB）＋ NonAROCR（自定 PyTorch，13MB） |
| 讀法 | 整張 strip 各「分類」一次 → 輸出類別 | 找字框→裁 200×80→每字元位置獨立分 12 類 → **輸出字串** |
| 新模號 | 必是訓練過的類別（不然讀錯） | 理論 open-vocab（實務仍建議重訓，HANDOFF §6.4） |
| NG | 有 NG 類（雙 head 都 NG 才拒收） | **沒有 NG 類**——靠信心門檻 needs_review + 人工複檢 |
| Pass | Passes=1/2（接縫修正） | **固定單次**（環狀 wrap 取窗，裁切階段已解接縫） |
| 精度 | M101 180/180 | val 99.96%（2415/2416）；錯誤2 集救回 34/35 |
| 延遲 | CPU 136-385ms | **GPU(3080) 14ms**；CPU 未量測 |
| 格式 | ONNX（ORT 推論） | **PyTorch .pt**（detector=ultralytics ckpt；NonAR=state_dict ckpt 含 val_acc） |

## 2. 前處理：同底座、不同後段（⚠ 不可混用現行參數）

```
共用底座（常數與 V6.7 一致，imgsz=640 / r_inner=0.6）：
  raw → crop_roi → Hough 定圓 → white_pad_square → annulus_polar(do_rotate=False) → 640×640 strip
CRNN 專屬後段（雙 head cls 沒有的）：
  → detector 取 cls0/cls1 最高信心框中心 x（conf 門檻 CRNN_DET_CONF=0.10）
  → band = strip[280:360]（80×640 字帶）
  → 以中心 x 環狀 wrap 裁兩個 200×80（HALF_W=100，不可改）
  → NonAR → (2,4,12) logits → decode（alphabet：blank+M+0-9）
  → 信心 = 解碼字元中最低的 softmax（最弱字決定可信度）
```
train/infer 一致性靠「直接 import 訓練當下那份 `crnn_dataset.py`/`nonar_model.py`」——任何移植都要逐位驗證。

## 3. 部署形態現況

- OCR_demo 已有 **`--serve` 子行程協定**（stdin/stdout JSON：`SERVER_READY`→請求→`RESULT_JSON`→`EXIT`），**本來就是設計給 .NET 對接的**；引擎可切 OcrEngine/CrnnEngine，對外介面相同（predict→OcrResult）。
- 相依：conda `lens-gpu`（torch 2.6+cu124／ultralytics／opencv）；GPU 建議（RTX 3050 已驗證可推）。

## 4. 接入路線（待拍板）

- **路線 A｜只進倉庫（版控/收發）**：registry 加 task `ocr_crnn`，files=[`detector.pt`,`nonar.pt`]。改動小；但 ⚠ 今日加的「PK 防呆」會擋 .pt——需改成 **per-task 內容規則**（此 task 的 .pt 是合法內容）。推論仍在 OCR_demo 側。
- **路線 B｜Server ONNX 化**：detector 可 ultralytics export；NonAR 要自寫 torch.onnx.export（transformer decoder，opset 17 應可）；**C# 重寫** detector 後處理＋band/wrap/decode。工程量大＋一致性驗證重，且 CPU 延遲未知。
- **路線 C｜Python sidecar**：AIVision.Api 掛 CRNN 子行程（沿用 `--serve` 協定），`POST /api/infer/ocr_crnn` 轉發。最快打通、天然 train/infer 一致；代價=server 機要 python 環境＋多一層行程管理。

**初步建議**：A＋C（倉庫先納管版本、推論用 sidecar 快速打通）；B 留到 GPU server（A1000）定案後評估是否值得。

## 5. 開放問題

1. CRNN 定位：與雙 head cls **並存互補**（fallback/仲裁）還是**取代**？（HANDOFF 稱 crnn_fallback；錯誤2 集救回 97% 暗示互補價值）
2. 無 NG 類 → 邊緣「兩 head 都 NG 才拒收」邏輯對 CRNN 不成立，需靠 needs_review 門檻——生產語意要另訂。
3. CPU 延遲未量：若 server 暫無 GPU，14ms 的優勢不保證存在。
4. 版本治理：兩顆 .pt 的 md5/發布/回滾可直接沿用現行機制（task 化已就緒）。
