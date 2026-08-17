# IDS peak 相機參數開發指南（C# / .NET 8 / WPF）

> 目標：在 .NET 8 + WPF 專案中，**穩定讀寫**以下四個關鍵參數，並提供安全的程式模板與除錯指引：
>
> * **AcquisitionLineRate**（線掃描）
> * **Height / HeightMax**（ROI 高度）
> * **ExposureTime**（曝光時間）
> * **Gain**（增益）

---

## 0. 適用範圍與前置準備

* **作業系統**：Windows x64。
* **Framework**：.NET 8（WPF）。
* **相機 SDK**：IDS peak（建議安裝 *Runtime x64*）。
* **架構假設**：你已能開啟 `device` 並取得 `var nodeMap = device.RemoteDevice().NodeMaps()[0];`。

### 0.1 專案設定（x64 + 複製 SDK 檔案，可二選一）

**方案 A（建議）**：使用系統安裝路徑

* 安裝 **IDS peak runtime x64**。
* 在 `appsettings.Development.json`（或 `appsettings.json`）新增：

```json
{
  "Camera": {
    "Providers": {
      "IdsPeak": {
        "Enabled": true,
        "SdkDir": "C:\\Program Files\\IDS\\ids_peak_runtime"
      }
    }
  }
}
```

**方案 B**：隨專案攜帶 DLL

* 建立資料夾：`AIVision.Presentation.Wpf/libs/cameras/IDS_PEAK/`
* 從安裝目錄複製需要的 **.NET wrapper** 與 **native DLL（bin\x64）** 到上述資料夾。
* `.csproj` 加入（確保 x64、複製輸出）：

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Platforms>x64</Platforms>
  <PlatformTarget>x64</PlatformTarget>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>

<ItemGroup>
  <Content Include="libs\cameras\IDS_PEAK\**\*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>

<Target Name="CopyIdsPeak" AfterTargets="Build">
  <ItemGroup>
    <IdsFiles Include="$(ProjectDir)libs\cameras\IDS_PEAK\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(IdsFiles)"
        DestinationFolder="$(OutDir)libs\cameras\IDS_PEAK\%(RecursiveDir)"
        SkipUnchangedFiles="true" />
</Target>
```

### 0.2 SDK 路徑解析（程式碼摘要）

> 在程式啟動（讀/寫任何節點之前）呼叫一次，確保 native loader 找得到 DLL。

```csharp
using System.Runtime.InteropServices;

static class NativeSearchPath
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        SetDllDirectory(path);
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!current.Split(';').Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", path + ";" + current);
    }
}

static void InitIdsPeakSdk(IConfiguration cfg, ILogger logger)
{
    var configured = cfg["Camera:Providers:IdsPeak:SdkDir"];
    var candidates = new []
    {
        configured,
        Path.Combine(AppContext.BaseDirectory, "libs","cameras","IDS_PEAK"),
        Environment.GetEnvironmentVariable("IDS_PEAK_SDK_DIR"),
        @"C:\\Program Files\\IDS\\ids_peak_runtime",
        @"C:\\Program Files\\IDS\\ids_peak"
    }
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(Path.GetFullPath)
    .ToList();

    var sdkDir = candidates.FirstOrDefault(Directory.Exists)
        ?? throw new DirectoryNotFoundException($"IDS peak SDK path not found. Tried: {string.Join(" | ", candidates)}");

    NativeSearchPath.Add(sdkDir);
    var x64 = Path.Combine(sdkDir, "bin", "x64");
    NativeSearchPath.Add(x64);
    logger?.LogInformation("IDS peak SDK resolved: {SdkDir}; x64={X64}", sdkDir, Directory.Exists(x64));
}
```

---

## 1. 參數快速總覽

| 參數                      | 作用                                             | 單位   | 可寫條件 / 限制                                                   | 備註                |
| ----------------------- | ---------------------------------------------- | ---- | ----------------------------------------------------------- | ----------------- |
| **AcquisitionLineRate** | （僅 **Linescan**）每秒掃描的行數                        | Hz   | `SensorOperationMode=Linescan`；`TriggerMode(LineStart)=Off` | 行頻↑ → 每行曝光時間↓、雜訊↑ |
| **Height / HeightMax**  | 影像/ROI 的垂直像素高度；`HeightMax` 為壓縮後最大高度（唯讀）        | px   | `Height ≤ HeightMax` 且符合 `Increment`                        | 調小可增 FPS/降資料量     |
| **ExposureTime**        | 曝光時間                                           | μs   | 受 Min/Max/Increment；可能需先停擷取                                 | 曝光↑ 亮度↑ 但易拖影      |
| **Gain**                | 訊號放大（Analog / Digital / Combined；Master/R/G/B） | 相機定義 | 需選 `GainSelector` 後設定                                       | 增益↑ 噪訊↑、動態範圍↓     |

> **關鍵原則**：**所有上下限與步階皆由機型/模式動態決定**。務必以 Node API 即時查詢 `Minimum/Maximum/Increment`。

---

## 2. 範圍讀取模板（一次抓四參數的 Range）

> 檔名建議：`Services/Cameras/IdsPeakRangeReader.cs`

```csharp
using System;
using System.Linq;
using System.Collections.Generic;
using peak.core.nodes;

public record RangeF(double Min, double Max, double Inc, bool HasInc);
public record RangeI(long Min, long Max, long Inc, bool HasInc);

public record CameraParamRanges(
    RangeF? AcquisitionLineRate,
    RangeI? Height,
    long?   HeightMax,
    RangeF? ExposureTime,
    Dictionary<string, RangeF> GainBySelector
);

public static class IdsPeakRangeReader
{
    private static RangeF? ReadFloatRange(FloatNode node)
    {
        if (node == null || !node.IsReadable) return null;
        double min = node.Minimum();
        double max = node.Maximum();
        bool has = node.HasConstantIncrement();
        double inc = has ? node.Increment() : 0.0;
        return new RangeF(min, max, inc, has);
    }

    private static RangeI? ReadIntRange(IntegerNode node)
    {
        if (node == null || !node.IsReadable) return null;
        long min = node.Minimum();
        long max = node.Maximum();
        bool has = node.HasConstantIncrement();
        long inc = has ? node.Increment() : 1;
        return new RangeI(min, max, inc, has);
    }

    public static CameraParamRanges ReadAll(peak.core.nodemap.NodeMap nodeMap)
    {
        var lineRateNode = nodeMap.FindNode<FloatNode>("AcquisitionLineRate");
        var lineRate = (lineRateNode != null && lineRateNode.IsReadable) ? ReadFloatRange(lineRateNode) : null;

        var heightNode    = nodeMap.FindNode<IntegerNode>("Height");
        var heightMaxNode = nodeMap.FindNode<IntegerNode>("HeightMax");
        var height        = heightNode != null ? ReadIntRange(heightNode) : null;
        long? heightMax   = (heightMaxNode != null && heightMaxNode.IsReadable) ? heightMaxNode.Value() : null;

        var expNode = nodeMap.FindNode<FloatNode>("ExposureTime");
        var exposure = expNode != null ? ReadFloatRange(expNode) : null;

        var gainMap = new Dictionary<string, RangeF>(StringComparer.OrdinalIgnoreCase);
        var gainSelector = nodeMap.FindNode<EnumerationNode>("GainSelector");
        var gainValue    = nodeMap.FindNode<FloatNode>("Gain") ?? nodeMap.FindNode<FloatNode>("AnalogGain");

        if (gainSelector != null && gainSelector.IsReadable && gainValue != null)
        {
            foreach (var entry in gainSelector.Entries().Select(e => e.Symbolic()))
            {
                try { gainSelector.SetCurrentEntry(entry); } catch { continue; }
                if (gainValue.IsReadable)
                {
                    var r = ReadFloatRange(gainValue);
                    if (r != null) gainMap[entry] = r;
                }
            }
        }
        else if (gainValue != null && gainValue.IsReadable)
        {
            var r = ReadFloatRange(gainValue);
            if (r != null) gainMap["(default)"] = r;
        }

        return new CameraParamRanges(lineRate, height, heightMax, exposure, gainMap);
    }
}
```

**使用方式**

```csharp
// 取得 nodeMap 後（必要時先停擷取）
var ranges = IdsPeakRangeReader.ReadAll(nodeMap);

// 範例輸出
if (ranges.AcquisitionLineRate != null)
    Console.WriteLine($"LineRate: {ranges.AcquisitionLineRate.Min} ~ {ranges.AcquisitionLineRate.Max} Hz" +
                      (ranges.AcquisitionLineRate.HasInc ? $" (inc={ranges.AcquisitionLineRate.Inc})" : ""));

if (ranges.Height != null)
    Console.WriteLine($"Height: {ranges.Height.Min} ~ {ranges.Height.Max} px" +
                      (ranges.Height.HasInc ? $" (inc={ranges.Height.Inc})" : ""));
if (ranges.HeightMax.HasValue)
    Console.WriteLine($"HeightMax: {ranges.HeightMax.Value} px");

if (ranges.ExposureTime != null)
    Console.WriteLine($"Exposure: {ranges.ExposureTime.Min} ~ {ranges.ExposureTime.Max} µs" +
                      (ranges.ExposureTime.HasInc ? $" (inc={ranges.ExposureTime.Inc})" : ""));

foreach (var kv in ranges.GainBySelector)
    Console.WriteLine($"Gain[{kv.Key}]: {kv.Value.Min} ~ {kv.Value.Max}" +
                      (kv.Value.HasInc ? $" (inc={kv.Value.Inc})" : ""));
```

---

## 3. 通用設定 Helper（量化到合法值 + 停/啟擷取）

> 將 **「把值 snap 到合法範圍/步階」** 與 **「必要時停擷取→設值→復原」** 流程封裝，避免 `OUT_OF_RANGE` / `ACCESS_DENIED`。

```csharp
public static class CameraParamSetter
{
    private static double Snap(double value, double min, double max, double inc, bool hasInc)
    {
        value = Math.Clamp(value, min, max);
        if (hasInc && inc > 0)
            value = min + Math.Round((value - min) / inc) * inc;
        return Math.Clamp(value, min, max);
    }

    private static long Snap(long value, long min, long max, long inc, bool hasInc)
    {
        value = Math.Clamp(value, min, max);
        if (hasInc && inc > 0)
            value = min + ((value - min) / inc) * inc;
        return Math.Clamp(value, min, max);
    }

    public static void SetExposureUs(peak.core.nodemap.NodeMap nodeMap, double exposureUs, Func<bool>? stopAcq=null, Action<bool>? startAcq=null)
    {
        var node = nodeMap.FindNode<peak.core.nodes.FloatNode>("ExposureTime");
        if (node == null) throw new InvalidOperationException("ExposureTime node not found");
        if (!node.IsWritable) stopAcq?.Invoke();
        var r = (node.IsReadable) ? (node.HasConstantIncrement() ? (node.Minimum(), node.Maximum(), node.Increment(), true) : (node.Minimum(), node.Maximum(), 0.0, false)) : throw new InvalidOperationException("ExposureTime not readable");
        var v = Snap(exposureUs, r.Item1, r.Item2, r.Item3, r.Item4);
        node.SetValue(v);
        startAcq?.Invoke(true);
    }

    public static void SetHeight(peak.core.nodemap.NodeMap nodeMap, long height, Func<bool>? stopAcq=null, Action<bool>? startAcq=null)
    {
        var node = nodeMap.FindNode<peak.core.nodes.IntegerNode>("Height");
        var maxNode = nodeMap.FindNode<peak.core.nodes.IntegerNode>("HeightMax");
        if (node == null) throw new InvalidOperationException("Height node not found");
        if (!node.IsWritable) stopAcq?.Invoke();
        long maxAllowed = (maxNode != null && maxNode.IsReadable) ? maxNode.Value() : node.Maximum();
        long min = node.Minimum();
        long max = Math.Min(node.Maximum(), maxAllowed);
        long inc = node.HasConstantIncrement() ? node.Increment() : 1;
        var v = Snap(height, min, max, inc, node.HasConstantIncrement());
        node.SetValue(v);
        startAcq?.Invoke(true);
    }

    public static void SetLineRateHz(peak.core.nodemap.NodeMap nodeMap, double hz, Func<bool>? stopAcq=null, Action<bool>? startAcq=null)
    {
        var node = nodeMap.FindNode<peak.core.nodes.FloatNode>("AcquisitionLineRate");
        if (node == null) throw new InvalidOperationException("AcquisitionLineRate node not found (Linescan only)");
        if (!node.IsWritable) stopAcq?.Invoke();
        double min = node.Minimum();
        double max = node.Maximum();
        double inc = node.HasConstantIncrement() ? node.Increment() : 0.0;
        var v = Snap(hz, min, max, inc, node.HasConstantIncrement());
        node.SetValue(v);
        startAcq?.Invoke(true);
    }

    public static void SetGain(peak.core.nodemap.NodeMap nodeMap, string selector, double gainValue, Func<bool>? stopAcq=null, Action<bool>? startAcq=null)
    {
        var sel = nodeMap.FindNode<peak.core.nodes.EnumerationNode>("GainSelector");
        var val = nodeMap.FindNode<peak.core.nodes.FloatNode>("Gain") ?? nodeMap.FindNode<peak.core.nodes.FloatNode>("AnalogGain");
        if (val == null) throw new InvalidOperationException("Gain node not found");
        if (!val.IsWritable) stopAcq?.Invoke();
        if (sel != null && sel.IsWritable) sel.SetCurrentEntry(selector);
        double min = val.Minimum();
        double max = val.Maximum();
        double inc = val.HasConstantIncrement() ? val.Increment() : 0.0;
        var v = Snap(gainValue, min, max, inc, val.HasConstantIncrement());
        val.SetValue(v);
        startAcq?.Invoke(true);
    }
}
```

> **整合建議**：把 `stopAcq` 與 `startAcq` 綁定到你的相機擷取控制（如 `Stop()` 與 `Start()`），當遇 `AccessDenied` 或 `IsWritable == false` 時自動停擷取後設值。

---

## 4. 推薦設定流程（實務順序）

1. **決定模式**：Area Scan 或 Linescan（僅 Linescan 有 `AcquisitionLineRate`）。
2. **設定 ROI**：以 `Height`（與 `Width`）縮 ROI → 提升 FPS、降資料量。
3. **曝光為主**：以 `ExposureTime` 拉到不過曝、不拖影為原則。
4. **Gain 微調**：最後用 `Gain` 小幅提亮；避免大增益導致噪訊。
5. **Linescan 才調行頻**：平衡輸送速度 / 行頻 / 訊噪。
6. **遵守 Increment**：寫入前先用上述 `Snap()` 量化到合法步階。

---

## 5. 範例：一次設定四參數（Area Scan 範例）

```csharp
// 先讀範圍（必要時先 StopAcquisition）
var ranges = IdsPeakRangeReader.ReadAll(nodeMap);

// 停/啟擷取委派（視你的相機控制類別實作）
bool Stop() { /* camera.Stop(); */ return true; }
void Start(bool _) { /* camera.Start(); */ }

// 1) ROI 高度 1024 px
CameraParamSetter.SetHeight(nodeMap, 1024, Stop, Start);

// 2) 曝光 10ms（10000us）
CameraParamSetter.SetExposureUs(nodeMap, 10_000, Stop, Start);

// 3) 增益（All/Master）6 dB
CameraParamSetter.SetGain(nodeMap, "All", 6.0, Stop, Start);

// Linescan 模式才會用到：
// CameraParamSetter.SetLineRateHz(nodeMap, 2000.0, Stop, Start);
```

---

## 6. 常見錯誤對照表

| 錯誤/症狀                                                     | 可能原因                                 | 排除建議                                                      |
| --------------------------------------------------------- | ------------------------------------ | --------------------------------------------------------- |
| `DirectoryNotFoundException: IDS peak SDK path not found` | DLL 搜尋路徑未加入                          | 依「0.2 SDK 路徑解析」加入安裝路徑與 `bin/x64`；或使用方案 B 複製並在輸出目錄保留結構     |
| `PEAK_STATUS_ACCESS_DENIED` / `IsWritable=false`          | 影像擷取中，節點鎖定                           | 先 `StopAcquisition()` 再設值，完成後重新開始                         |
| `PEAK_STATUS_OUT_OF_RANGE`                                | 沒遵守 Min/Max/Increment                | 用 `Snap()` 量化；先讀 `Minimum/Maximum/Increment`              |
| `PEAK_STATUS_VALUE_ADJUSTED`                              | 設值被自動修正                              | 顯示最終值提示使用者；或改以 increment 對齊                               |
| 找不到 `AcquisitionLineRate`                                 | 非 Linescan 模式                        | 切換 `SensorOperationMode=Linescan`，並確保 `LineStart` 觸發為 Off |
| `HeightMax` 太小                                            | 啟用 binning/decimation/像素格式導致垂直可用高度變小 | 先關閉壓縮或調整像素格式；再重讀 `HeightMax`                              |

---

