下面是一份可直接貼進 repo 的 **C# 開發指南.md**（繁中），涵蓋硬體要點、SDK 安裝與路徑、Visual Studio 設定、四個關鍵參數（LineRate / Height / Exposure / Gain）的讀寫範例、常見地雷與效能建議，並附上一個最小化的 `CameraConfigurator.cs`（.NET / IDS peak .NET binding）骨架。你可以把整段存成 `docs/IDS-peak-CSharp-開發指南.md`，並將 `src/Infrastructure/Camera/CameraConfigurator.cs` 放入專案使用。

---

# IDS uEye CP（USB 3）+ IDS peak SDK

# C# 完整開發指南

> 適用：Windows 10/11 x64、Visual Studio 2022、.NET 6/7/8、IDS peak **Generic SDK** + **.NET binding**
> 目標：在程式中**可調整四個關鍵參數**（`AcquisitionLineRate`、`Height`、`ExposureTime`、`Gain`），並能穩定啟停擷取。

---

## 1) 硬體與佈線快速檢查

* **相機本體**：USB3 介面之 uEye CP Rev.2.x（型號依你手邊實機）。
* **鏡頭/光源**：依作業距離與 DOF 選定焦距、光圈；建議固定光源避免自動曝光來回調。
* **USB 3.0 纜線**：盡量使用短、品質佳的 **SuperSpeed (5 Gbps)** 線；若超長或經延長器，優先用有源延長。
* **主機端**：確保連在 **USB 3 控制器**（不是 2.0 Hub），並關閉省電（裝置管理員 → USB Root Hub → 取消允許電腦關閉）。
* **I/O 觸發/閃光（選用）**：相機的 GPIO/Flash 走外接 I/O 端子（視型號腳位）；如要用硬觸發，請備妥對應接頭與電位（TTL/OC）。
* **機械固定/遮光**：抑制震動與雜散光，避免曝光抖動與亮暗帶。

---

## 2) SDK 與工具

安裝 **IDS peak** 後，預設路徑（依版本略有差異）：

```
C:\Program Files\IDS\ids_peak\
  ├─ program\                 ← Peak Cockpit 等工具的執行檔
  ├─ generic_sdk\             ← ★ 開發重點：標頭 / lib / .NET / 範例
  │   ├─ api\
  │   │   ├─ include\         ← C/C++ 標頭
  │   │   ├─ lib\x86_64\      ← C/C++ .lib
  │   │   └─ binding\dotnet\x86_64\  ← ★ .NET 封裝 DLL（ids_peak_dotnet.dll 等）
  │   ├─ ipl\ ...（影像處理選用）
  │   ├─ afl\ ...（演算法選用）
  │   └─ samples\source\csharp\ ← C# 範例（open_camera / simple_live_wpf…）
  └─ ...
```

**常用工具**

* **IDS peak Cockpit**：先驗證相機、韌體與影像流是否正常。
* **ids_deviceupdate**：必要時更新韌體。
* **Samples（C#/C++/Python）**：`open_camera`, `simple_live_wpf` 是最佳起手式。

---

## 3) Visual Studio 專案設定（C#）

1. 新建 **.NET 6/7/8（Console 或 WPF）** 專案，**Platform target 設 x64**。
2. 參考（Add Reference → Browse）加入：

   * `…\generic_sdk\api\binding\dotnet\x86_64\ids_peak_dotnet.dll`
   * 若要用影像處理/演算法，再加：

     * `ids_peak_ipl_dotnet.dll`
     * `ids_peak_afl_dotnet.dll`
3. 執行環境路徑：若執行期找不到原生 DLL，可在「專案 → Debug → Environment」加入：

   ```
   PATH=%PATH%;C:\Program Files\IDS\ids_peak\generic_sdk\bin\x86_64
   ```

   （或把該資料夾加入系統 PATH）

---

## 4) 四個關鍵參數（GenICam 節點）

| 參數                        | 節點名                     | 類型           | 注意事項                                                                                         |
| ------------------------- | ----------------------- | ------------ | -------------------------------------------------------------------------------------------- |
| **Acquisition Line Rate** | `AcquisitionLineRate`   | Float (Hz)   | 多用於**線掃相機**。設定前需 `TriggerSelector=LineStart` 且 `TriggerMode=Off`（否則被觸發模式接管）。若為面陣機種，該節點可能不存在。 |
| **Height**                | `Height`                | Integer (px) | ROI 尺寸。修改前建議**停止擷取**並在修改後**重建 Buffer/再啟動**。最大值看 `HeightMax`，並依 `Increment()` 對齊。             |
| **Exposure Time**         | `ExposureTime`          | Float (µs)   | 手動前請 `ExposureMode=Timed`、`ExposureAuto=Off`。                                                |
| **Gain**                  | `Gain` + `GainSelector` | Float + Enum | 常見 selector：`AnalogAll`/`DigitalAll`/RGB channel… 手動時 `GainAuto=Off`。增益範圍依機型。                |

> 註：任何節點都需先檢查 **是否存在/可寫**（不同機型韌體略有差異），並在不支援時優雅退回。

---

## 5) 最小化：開機 → 設參數 → 啟停擷取

> 下列骨架聚焦**節點設定**與**安全流程**；實際影像擷取（DataStream / Buffer / Callback）可直接參考官方 `open_camera` / `simple_live_wpf`，把「設定段」嵌入在 **開啟裝置後、啟動擷取前**。

**檔名**：`src/Infrastructure/Camera/CameraConfigurator.cs`

```csharp
// 需要參考：ids_peak_dotnet.dll（x64）
// 假設使用 .NET 6+，請依你的 samples 引入相同命名空間。
// 常見 using（依 SDK 版本可能略有差異，請對照 samples/open_camera）：
// using peak;
// using peak.core;
// using peak.core.nodes;

using System;

namespace ToroTech.Camera
{
    public static class CameraConfigurator
    {
        /// <summary>
        /// 設定四個關鍵參數（任何一項傳 null 代表跳過）
        /// call 時機：Open Device -> set params -> (re)alloc buffers -> Start Acquisition
        /// </summary>
        public static void Configure(
            dynamic device,               // peak.Device
            double? exposureUs,           // 例如 15000 (15ms)
            (string selector, double value)? gain, // 例如 ("AnalogAll", 3.0)
            long? heightPx,               // 例如 1024
            double? lineRateHz            // 例如 30000（若為面陣或不支援，可傳 null）
        )
        {
            var nm = device.RemoteDevice().NodeMaps()[0];

            // ---------- 曝光 ----------
            if (exposureUs.HasValue)
            {
                TrySetEnum(nm, "ExposureMode", "Timed"); // Timed 曝光
                TrySetEnum(nm, "ExposureAuto", "Off");   // 手動模式
                TrySetFloat(nm, "ExposureTime", exposureUs.Value);
            }

            // ---------- 增益 ----------
            if (gain.HasValue)
            {
                TrySetEnum(nm, "GainAuto", "Off");
                TrySetEnum(nm, "GainSelector", gain.Value.selector);
                TrySetFloat(nm, "Gain", gain.Value.value);
            }

            // ---------- 影像高度（ROI） ----------
            if (heightPx.HasValue)
            {
                // 注意：應在 Stop Acquisition 之後、Start 之前呼叫
                TrySetIntegerAligned(nm, "Height", heightPx.Value);
            }

            // ---------- 線頻（Line Scan 適用） ----------
            if (lineRateHz.HasValue)
            {
                // 關閉 LineStart 觸發，才可直接寫入線頻
                TrySetEnum(nm, "TriggerSelector", "LineStart");
                TrySetEnum(nm, "TriggerMode", "Off");
                TrySetFloat(nm, "AcquisitionLineRate", lineRateHz.Value);
            }
        }

        // ----------------- 共用工具：安全寫節點 -----------------
        private static void TrySetEnum(dynamic nm, string node, string entry)
        {
            var en = nm.FindNode<peak.core.nodes.EnumerationNode>(node);
            if (en != null && en.IsWritable() && en.HasEntry(entry))
                en.SetCurrentEntry(entry);
        }

        private static void TrySetFloat(dynamic nm, string node, double value)
        {
            var fn = nm.FindNode<peak.core.nodes.FloatNode>(node);
            if (fn != null && fn.IsWritable())
            {
                var v = Clamp(value, fn.Minimum(), fn.Maximum());
                fn.SetValue(v);
            }
        }

        private static void TrySetIntegerAligned(dynamic nm, string node, long value)
        {
            var n = nm.FindNode<peak.core.nodes.IntegerNode>(node);
            if (n != null && n.IsWritable())
            {
                long min = n.Minimum();
                long max = n.Maximum();
                long inc = Math.Max(1, n.Increment());

                long aligned = value - (value % inc);
                aligned = Math.Clamp(aligned, min, max);
                n.SetValue(aligned);
            }
        }

        private static double Clamp(double v, double min, double max)
            => Math.Max(min, Math.Min(max, v));
    }
}
```

**使用流程（示意）**

```csharp
// 1) 初始化與開機（可參考 samples/open_camera）
// peak.Library.Initialize();
// var system = ...; var device = ...; device.Open();

// 2) 停止擷取（若正在跑）
/*
dataStream.StopAcquisition();
device.RemoteDevice().NodeMaps()[0]
    .FindNode<peak.core.nodes.CommandNode>("AcquisitionStop")?.Execute();
*/

// 3) 設定參數
ToroTech.Camera.CameraConfigurator.Configure(
    device,
    exposureUs: 15000,                 // 15 ms
    gain: ("AnalogAll", 2.5),          // 模擬/類比增益 2.5（依機型上限為準）
    heightPx: 1024,                    // ROI 高度
    lineRateHz: null                   // 面陣機種可 null；線掃才設定
);

// 4)（重要）若有改 ROI：重建 Buffer（參考 samples）
// dataStream.AllocAndAnnounceBuffers(...);

// 5) 啟動擷取
/*
dataStream.StartAcquisition();
device.RemoteDevice().NodeMaps()[0]
    .FindNode<peak.core.nodes.CommandNode>("AcquisitionStart")?.Execute();
*/

// 6) 擷取迴圈（略，請複用 open_camera 的 buffer 取得/回收邏輯）
```

> **為何把擷取步驟註解？**
> IDS 官方 samples 的 **DataStream/Buffer 管理**已寫好、而且版本差異會影響 API 細節。做法是：**直接複製官方 `open_camera` 或 `simple_live_wpf` 的啟停與取 Buffer 程式塊**，然後把上面的「設定段」插在「Open 之後、Start 之前」，即可 0 風險整合。

---

## 6) 常見地雷與修復

1. **專案是 AnyCPU / x86** → 轉 **x64**。
2. **找不到 ids_peak 原生 DLL** → 把 `…\generic_sdk\bin\x86_64` 加到 **PATH** 或 VS Debug Environment。
3. **節點不存在或不可寫** → 該機型/模式不支援，或需先關閉自動（`ExposureAuto/GainAuto=Off`）、停擷取、或更換 `TriggerSelector`。使用前先 `IsReadable/IsWritable()` 判斷。
4. **改 ROI 後影像不出來** → 一定要 **Stop → 設參數 → 重新分配 Buffer → Start**。
5. **LineRate 設不進去** → 通常因為還在觸發模式（`TriggerMode=On`）。切到 `TriggerSelector=LineStart` 並 `TriggerMode=Off`。且面陣機種多沒有此節點。
6. **USB 間歇斷訊** → 換短線/有源延長、插主機板的原生 USB3 埠、關 USB 省電，必要時獨立控制器。

---

## 7) 效能與畫質建議

* **固定光源** + 手動 `ExposureTime/Gain`，可避免自動調整造成抖動。
* **縮 ROI（Height/Width）** 可降傳輸量、提升 FPS。
* **PixelFormat**：若傳輸瓶頸，改成 8-bit 單通道（例如 Mono8）；色彩才用 Bayer/RGB。
* **Buffer 數量**：至少 3 個（Triple Buffering），高速下適度增加。
* **CPU/GPU**：若後續有運算（OpenCV/AI），建議影像處理與擷取在不同執行緒。

---

## 8) 與你現有架構（Clean Architecture）接合

**Application（UseCases）**

```csharp
public record SetCameraParamsCommand(
    double? ExposureUs, (string Selector, double Value)? Gain, long? HeightPx, double? LineRateHz);

public interface ICameraPort
{
    void SetParameters(SetCameraParamsCommand cmd);
    // 其他：Open/Close/Start/Stop/Grab 等
}
```

**Infrastructure（Adapter：IDS peak）**

```csharp
public sealed class PeakCameraAdapter : ICameraPort, IDisposable
{
    private dynamic _device;  // 以 samples 建立的 device/dataStream

    public void SetParameters(SetCameraParamsCommand cmd)
    {
        ToroTech.Camera.CameraConfigurator.Configure(
            _device, cmd.ExposureUs, cmd.Gain, cmd.HeightPx, cmd.LineRateHz);
        // 若 Height 有變更 → 重新 alloc buffers（複用 samples 程式碼）
    }

    // Open/Start/Stop/Dispose …（直接搬官方 open_camera 流程）
}
```

**Presentation（WPF 或 API）**

* 建一個簡單表單可改四參數；輸入時就地做 **範圍/步階對齊**（`Minimum/Maximum/Increment`）。
* 寫個「讀取目前值」的 query，顯示當前狀態（可用 `GetValue()` 讀回節點）。

---

## 9) 驗證清單（出廠自測）

1. Cockpit 能看到畫面且無掉幀/斷線。
2. 跑 `samples\source\csharp\open_camera`：可啟停、存檔。
3. 在程式中依序測：

   * `ExposureAuto=Off` 後能設定 `ExposureTime`。
   * `GainAuto=Off` + `GainSelector=AnalogAll` 後能設定 `Gain`。
   * `Stop` → 設 `Height` → 重配 Buffer → `Start` 後影像正確。
   * 線掃機：`TriggerSelector=LineStart` + `TriggerMode=Off` 能設定 `AcquisitionLineRate`。
4. 長跑 30–60 分鐘：溫升後仍穩定（USB 線不鬆動、無省電介入）。

---

## 10) 附錄：常用節點對照

* 曝光：`ExposureMode`（Timed）、`ExposureAuto`（Off/Continuous/Once）、`ExposureTime`（µs）
* 增益：`GainAuto`、`GainSelector`（AnalogAll/DigitalAll/R/G/B…）、`Gain`
* ROI：`Width` / `Height` / `OffsetX` / `OffsetY`（注意 `Increment()`）
* 觸發：`TriggerSelector`（FrameStart/LineStart/…）、`TriggerMode`（On/Off）、`TriggerSource`
* 線頻（線掃）：`AcquisitionLineRate`
* 影像格式：`PixelFormat`（Mono8/BayerRG8/RGB8…）

---

### 你接下來可以做什麼？

* 直接把上面的 `CameraConfigurator.Configure(...)` 嵌入你現有 **open → set → (re)alloc → start** 的流程。
* 想要我幫你把 **官方 `open_camera`** 改造成「你專案的 `PeakCameraAdapter`（含擷取與 Buffer 管理）」嗎？我可以直接給一份可編譯版本，介面就照上面 `ICameraPort`，把四參數做成 command，WPF 端做成一頁面即可。
