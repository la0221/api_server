---
date: 2026-07-16
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 「線上模型管理 (AINAVI API)」這條線：現況盤點與打通規劃
status: proposal（規劃，未實作；等使用者拍板 §7 的前提問題）
tags: [AINAVI, EdgeHub, 瑕疵檢測, 線上模型, 孤兒功能, 技術債, 規劃]
---

# 設計書：AINAVI EdgeHub 線上模型這條線

> 觸發：使用者看到「線上模型管理 (AINAVI API)」視窗，問「這東東可以幹啥？我要怎樣打通？」
> 本文件只盤點＋規劃，**不含實作**。

---

## 0. 三句話結論

1. **這頁現在是一支「EdgeHub 遙控器」**：能叫遠端那台 AINAVI 機器「載入模型 / 關閉服務」，而且 HTTP 是真的會打出去。
2. **但載入的模型不會被本機任何推論用到** —— 推論路徑在 `App.xaml.cs:413` 被**刻意**綁死到本地 ONNX。這是**設計決定，不是 bug**。
3. **打通的第一關不是程式碼，是那台機器**：`192.168.1.95` 目前 **ping 不到、ARP 表裡沒有、5001/8001/9080 全關**——它根本不在區網上。

---

## 1. 這東西是什麼

**AINAVI EdgeHub** = 廠商（Spingence）的**第三方 Linux AI 推論盒子**，放在 `192.168.1.95`。模型檔放在它的 `/home/smasoft/Public/ainavi_edgehub/models/` 下。

它跟我們這幾天做的中央推論**是兩條平行、互不相干的線**：

| | **AINAVI EdgeHub 線**（本文件） | **中央推論線**（07-14~16 在做的） |
|---|---|---|
| 用途 | **瑕疵檢測**（OK/NG、分割、分級） | **模號穴號 OCR** |
| 推論在哪 | 外部第三方盒子 192.168.1.95 | 自建 `AIVision.Api` |
| Port（介面） | `IAiInferencePort`（回 `Prediction`） | `IMoldCodePairRecognizerPort`（回 `PairObservation`） |
| 模型 | EdgeHub 上的 WPC_top / demo_seg / Ecoco… | 本地 `mohao.onnx` + `xuehao.onnx` |
| 現況 | **孤兒**（見 §3） | 階段 0-2 已打通 |

> 程式碼裡有句話直接點明兩者無關 —— `App.xaml.cs:145-146`：
> 「辨識器走獨立 port(IMoldCodeRecognizerPort)，**不經舊 IAiInferencePort(OCR≠瑕疵)**。」

`models.online.json` 裡 5 個模型全是瑕疵/分級，**沒有一個是模號穴號**：

| 模型 | 類型 | 輸出類別 |
|---|---|---|
| Jarrly_P885_Workflow | Workflow (9080) | TF碰撞、表面凹痕（含 `defectFiltering` 面積過濾） |
| **WPC_top**（現用） | 分類 | 良品 / 不良品 |
| demo_seg_1 / 2 | 分割 | 位置偏移、殘留、短路、焊橋、繞線 / 刮痕、髒汙、裂痕 |
| Ecoco_cls_251103 | 分類 | A/B/C/D 級品 |

---

## 2. 六個按鈕實際做什麼

| 按鈕 | 真的打 EdgeHub？ | 行為 |
|---|---|---|
| **載入模型** | ✅ **是** | `DELETE :5001/services` → 等 500ms → `POST :5001/services/inference?sync=true`（body 是 JSON **陣列** `[{uuid, model_path, port}]`）。Workflow 走 `/service/workflow?sync=true`。逾時 30 秒 |
| **關閉服務** | ✅ **是** | `DELETE :5001/services` —— ⚠️ 關掉該 EdgeHub 上**所有**服務，不是只關選的那個（雖然要先選一個模型，但那只是為了取位址） |
| 重新整理 | ❌ | 讀本地 `models.online.json` |
| 新增 / 編輯 / 刪除模型 | ❌ | **只改本地 json**，完全不碰 EdgeHub。等於一本「連線參數通訊錄」 |

**兩個 port 的分工**：`5001` = EdgeHub 管理埠（服務開/關）；`8001` = 模型掛上去之後的推論埠；`9080` = Workflow 推論埠。

---

## 3. ⚠️ 為什麼它是孤兒（關鍵）

`App.xaml.cs:405-416` 的註解寫得很清楚：

```csharp
// 註冊 SwitchableAiInferencePort:仍保留註冊,因為部分 ViewModel/Service 以具體型別注入…
// 但它已不再綁定為 Devices.IAiInferencePort —— 推論一律走本地 ONNX。
services.AddSingleton<SwitchableAiInferencePort>();

// 設備推論 port 一律走本地 ONNX 模號辨識器…
// 無遠端伺服器時不會 hang。
services.AddSingleton<Devices.IAiInferencePort>(sp =>
    new LocalMoldCodeInferencePort(...));
```

**淨結果**：按「載入模型」→ 遠端真的把模型掛到 8001 → 跳「載入成功」→ **然後本機所有推論仍走本地 ONNX，那個模型不會被打**。

這頁的 XAML 自己也承認（`OnlineModelManagementView.xaml:43`）：
> 「模型來源：models.online.json（透過 EdgeHub 載入 / 關閉服務，**不影響本地離線辨識器**）」

**斷開的動機看得出來是「防當機」**：多處註解提到「無伺服器時不會 hang」「避免無伺服器時 hang 15-20 秒」。也就是**因為 EdgeHub 常常不在，所以把它拔掉讓 App 能獨立跑**。

其他孤兒跡象：
- `SwitchableAiInferencePort` 是 **write-only**：`OnlineModelManagementViewModel` 設定的端點/模式**全系統沒有讀取者**。
- `ModelLoaded` 事件**沒有任何訂閱者**。
- `ProjectInitializationService.cs:566-570` 連續 5 個 `_ = xxx;` **丟棄參數**，明講載入邏輯已移除。
- `ModelSelectorViewModel.cs:58`：「改為 **no-op** 並記錄」。
- `SwitchModelCommand` / handler 存在但**沒人送**；且 WPF **從未呼叫 `AddAinaviServices()`** → 沒註冊 `IAiModelPort` → **若真有人送就會 DI 解析失敗**。目前靠「沒人送」僥倖不炸。

---

## 4. 打通的障礙分三層（由外而內）

### 🔴 第一層：硬體/環境（**目前卡在這**）

2026-07-16 實測：
```
ping 192.168.1.95 ×5  → 完全無回應
ARP 表                → 查無此 IP（機器不在區網上）
port 5001 / 8001 / 9080 → 全部關閉/不可達
```
**沒有這台機器，下面兩層做完也沒用。** 這是採購/佈署/網路問題，不是寫程式能解的。

### 🟠 第二層：架構（**最需要想清楚的**）

**⚠️ 陷阱：不要讓 AINAVI 去搶 `IAiInferencePort`。**

直覺做法是「把 `App.xaml.cs:413` 的綁定改回 `SwitchableAiInferencePort` 就好」。**這會出事**：

| | 本地 ONNX（現在佔著 `IAiInferencePort`） | AINAVI EdgeHub |
|---|---|---|
| `Prediction.Label` 是什麼 | 模號字串（如 `M101/08`） | 瑕疵類別（如 `TF_crash`、`OK`/`NG`） |

兩者**輸出語意完全不同**。直接互換會讓 `ShellViewModel` / `OfflineInspectionService` 的 OK/NG 判定錯亂。

> **現況本身就是個設計異味**：`IAiInferencePort`（原本是**瑕疵**推論的 port）現在被 `LocalMoldCodeInferencePort`（**模號 OCR**）佔用。等於用 OCR 頂替了瑕疵檢測的位置。

**正解方向**：仿照模號那條線的做法 —— 模號有自己的 `IMoldCodeRecognizerPort`，**瑕疵檢測也該有自己獨立的 port**，而不是兩者搶同一個 `IAiInferencePort`。這是要先拍板的設計決定。

### 🟡 第三層：程式碼債（可修，但先確定前兩層）

| # | 問題 | 位置 |
|---|---|---|
| 1 | **硬編 IP 無視 config**：手動 `new AinaviOptions { Host = "http://192.168.1.95" }`，沒走 `Configure<T>` → WPF 的 `ModelBasePath`/`DefaultModel`/`LogPath` 全是 C# 預設值 | `App.xaml.cs:364-375` |
| 2 | **`ModelBasePath` 兩邊不一致**：API 是 `/files/spingence/...`，json 是 `/home/smasoft/...` | `Api/appsettings.json:40` vs `models.online.json` |
| 3 | **WPF appsettings 的 `Ainavi` 殘缺**：只有 2 個 key，且 key 名是 `DefaultPort` 而非 `DefaultModelPort` | `Wpf/appsettings.json:187-190` |
| 4 | `AinaviOptions.DefaultModelPort` 預設 **8009**，但實際全是 8001 | `AinaviOptions.cs` |
| 5 | **json 讀 bin 目錄**：App 內編輯的結果**會在下次 build 被原始檔覆蓋**（csproj `PreserveNewest`） | `OnlineModelManagementViewModel.cs:57` |
| 6 | **兩個陣列語意混雜**：「新增模型」寫進 `ManualModels`，但現有清單在 `models` | `ModelConfigService.cs:355` |
| 7 | **兩份幾乎逐行重複的實作**：WPF 的 `AinaviApiClient` vs API 的 `AinaviAiModelPort` | 兩處 |
| 8 | `AinaviApiClient` 自己 `new HttpClient()`（非 `IHttpClientFactory`） | `AinaviApiClient.cs:33` |
| 9 | `logger: null` → 這條線**完全無 log**，除錯困難 | `OnlineModelManagementViewModel.cs:58` |
| 10 | 註解與現況矛盾（`App.xaml.cs:257-258`、`:406` 皆已過時） | 兩處 |

**WPF 與 API 各自獨立打同一台 EdgeHub**（不是 WPF 透過 API）。曾經有「透過 API」的路徑（`AinaviApiClient.OpenModelAsync(modelName, port)` → `/api/ainavi/open-model`），但已標 `[Obsolete]` 且無人呼叫。

---

## 5. 分階段路線（**前提：§7 拍板後才啟動**）

| 階段 | 內容 | 前置 |
|---|---|---|
| **A. 環境** | 讓 192.168.1.95（或新位址）的 EdgeHub 上線、網路可達、確認 5001/8001 服務起來 | 硬體/採購/網路 |
| **B. 驗遙控器** | 用現有 UI 按「載入模型」→ 確認遠端真的掛起模型（這部分程式碼已完整，**不用改**） | A |
| **C. 架構拍板** | 決定瑕疵檢測的 port 歸屬（**新獨立 port**，不搶 `IAiInferencePort`） | §7 |
| **D. 接回推論** | 依 C 的決定，把 EdgeHub 推論接回某條真實路徑；沿用中央推論線的 `Enabled` 開關 + 驗收按鈕範式（見 `2026-07-15_edge_server_integration.md`） | C |
| **E. 還債** | §4 第三層那 10 項，優先 1/2/3/5（會直接導致「載入失敗」或「設定改了沒效」） | — |

> **可先做且無風險的**：階段 E 的第 2、3 項（設定不一致）——就算 EdgeHub 永遠不回來，修好也不虧。但**沒有 EdgeHub 就無法驗證**，所以建議仍等 A。

---

## 6. 「這東東可以幹啥」——最短回答

- **現在**：一支遙控器，能開關遠端 AINAVI 盒子上的瑕疵檢測服務。**但那台盒子現在不在線上，所以連遙控器都按不動。**
- **就算按得動**：載入的模型**不會被本機拿來檢測**（刻意斷開的）。
- **它原本要幹的事**：讓產線用 AINAVI 盒子做**瑕疵檢測**（OK/NG、刮痕、凹痕…），跟模號穴號 OCR 是兩回事。

---

## 7. ⚠️ 待使用者拍板（**在此之前不該動手**）

**最關鍵的問題不是「怎麼打通」，而是「要不要打通」。** 理由：這條線是被**刻意**斷開的、機器**不在**、用途**與現在主線無關**。

1. **【最關鍵】瑕疵檢測還在產品範圍內嗎？**
   目前主線是模號穴號 OCR（混料防呆）。瑕疵檢測是**另一個功能**。若短期不做 → 這條線該**明確標記為停用/移除**，而不是留著半死不活誤導後人。
2. **【硬體】那台 AINAVI EdgeHub 還在嗎？** 在哪？會回來嗎？換位址了嗎？（現在 192.168.1.95 完全不在區網上）
3. **【策略】若要做瑕疵檢測，還要靠 AINAVI 嗎？** 還是像模號那樣**自建**（我們已經有能跑 ONNX 的 `AIVision.Api` 了）？→ 這會決定是「修這條線」還是「用中央推論線的架構重做」。
4. **【架構】若保留 AINAVI**：瑕疵檢測要用**新的獨立 port**（推薦，不搶 `IAiInferencePort`）還是其他做法？
5. **【範圍】WPF 直打 EdgeHub，還是統一透過 `AIVision.Api`？**（目前直打；`[Obsolete]` 那條透過 API 的路已廢）

> **我的建議**：先回答 1 和 2。若答案是「短期不做瑕疵檢測 / 機器不會回來」，那最有價值的動作是**把這條線明確標示為停用**（UI 上標、或直接移除入口），而不是花力氣打通 —— 半死不活的功能會持續誤導每個看到它的人（本次就是一例）。

---

## 8. 檔案地標

- 本文件：`.ai\designs\2026-07-16_ainavi_edgehub_line.md`
- 中央推論線（**不同的線**，勿混淆）：`.ai\designs\2026-07-15_edge_server_integration.md`、`2026-07-12_api_server_deployment.md`
- UI：`AIVision.Presentation.Wpf\Views\OnlineModelManagementView.xaml`、`ViewModels\OnlineModelManagementViewModel.cs`
- EdgeHub 客戶端（WPF）：`AIVision.Presentation.Wpf\Services\AinaviApiClient.cs`
- EdgeHub 客戶端（API，重複實作）：`AIVision.Infrastructure\AiService\AinaviAiModelPort.cs`
- 模型清單：`AIVision.Presentation.Wpf\models.online.json`（實際讀 bin 那份）
- **推論路徑被切斷處**：`AIVision.Presentation.Wpf\App.xaml.cs:405-416`
- 硬編 IP：`App.xaml.cs:364-375`、`AinaviOptions.cs`、`Models\ModelConfig.cs:74`
- API 側端點：`AIVision.Api\Controllers\AinaviController.cs`
