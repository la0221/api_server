using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Infrastructure.Devices.Blow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 吹氣觸發設定（移植自 <c>模號檢驗/相機版</c> 的 BlowSettingsWindow）。
///
/// <para><b>存哪裡</b>：寫 <c>configs/blow.json</c>——`DelayMs` 這種要在現場邊試邊調的值，
/// 不該逼人去改 appsettings（那是部署設定，改了還要重啟）。該檔在 App 啟動時以
/// <c>reloadOnChange:true</c> 掛進設定系統，所以**存檔即生效、免重啟**。</para>
///
/// <para><b>測試吹氣</b>：現場裝機時最需要的一顆按鈕——不必等真的混料，
/// 直接送一發出去看吹嘴會不會動、延遲對不對。</para>
/// </summary>
public partial class BlowSettingsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly IBlowDispatcherPort _dispatcher;
    private readonly IBlowOutputPort _output;
    private readonly ILogger<BlowSettingsViewModel>? _logger;

    public BlowSettingsViewModel(
        IOptionsMonitor<BlowOptions> options,   // ⚠ 不能用 IOptions：它快取一輩子，存檔後重開視窗會看到舊值
        IBlowDispatcherPort dispatcher,
        IBlowOutputPort output,
        ILogger<BlowSettingsViewModel>? logger = null)
    {
        _dispatcher = dispatcher;
        _output = output;
        _logger = logger;

        var o = options.CurrentValue;
        Enabled = o.Enabled;
        DelayMs = o.DelayMs;
        BlowOnMismatch = o.BlowOnMismatch;
        BlowOnNg = o.BlowOnNg;
        Host = o.Host;
        Port = o.Port;
        Channel = o.Channel;
        ConnectTimeoutMs = o.ConnectTimeoutMs;
        UseLogOnly = string.Equals(o.Output, "Log", StringComparison.OrdinalIgnoreCase);
        UseJsonFormat = string.Equals(o.Format, "Json", StringComparison.OrdinalIgnoreCase);
        KeepAlive = o.KeepAlive;

        OutputName = _output.DisplayName;
        ConfigPath = ResolveConfigPath();
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _delayMs;
    [ObservableProperty] private bool _blowOnMismatch = true;
    [ObservableProperty] private bool _blowOnNg;
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port = 5001;
    [ObservableProperty] private int _channel;
    [ObservableProperty] private int _connectTimeoutMs = 1500;
    [ObservableProperty] private bool _useLogOnly;

    /// <summary>
    /// 訊號格式：勾起來＝送相機版那套 JSON；不勾＝送 NgAirBlowService 吃的 NG 純文字（預設）。
    /// <para>⚠ 這個勾錯是**無聲失敗**：JSON 整串沒有 NG 兩個字，NgAirBlowService 會當雜訊丟掉，
    /// 混料照判、log 照寫、就是不吹。</para>
    /// </summary>
    [ObservableProperty] private bool _useJsonFormat;

    /// <summary>長連線（對方的對接說明建議這樣）。</summary>
    [ObservableProperty] private bool _keepAlive = true;

    /// <summary>目前實際生效的輸出通道名稱（重啟後才會跟著 UseLogOnly 改變）。</summary>
    [ObservableProperty] private string _outputName = string.Empty;

    /// <summary>設定檔位置（現場要知道去哪備份／複製到別台）。</summary>
    [ObservableProperty] private string _configPath = string.Empty;

    [ObservableProperty] private string _statusText =
        "吹氣是判定之後的後段動作：送不出去只會記 log，不會影響辨識。";

    [RelayCommand]
    private void Save()
    {
        var o = ToOptions();
        o.Normalize();
        try
        {
            var path = ResolveConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 巢狀成 Devices:Blow，才能跟 appsettings 的同一段疊在一起。
            var doc = new { Devices = new { Blow = o } };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));

            ApplyBack(o);
            // 啟用/延遲/開關/位址都是 IOptionsMonitor 每次讀，存檔即生效；
            // 但「走 TCP 還是只寫 log」是在 DI 建立時決定的實例，要重開程式才換。
            StatusText = $"✔ 已儲存並即刻生效（免重啟）：{path}"
                       + "　※ 若改了『只寫 log』選項，要重開程式才會換通道";
            _logger?.LogInformation("[Blow] 設定已儲存：{Path}", path);
        }
        catch (Exception ex)
        {
            StatusText = $"⚠ 儲存失敗：{ex.Message}";
            _logger?.LogWarning(ex, "[Blow] 設定儲存失敗");
        }
    }

    /// <summary>
    /// 送一發測試吹氣。現場裝機時最需要的按鈕：不必等真的混料就能確認吹嘴會動、延遲對不對。
    /// </summary>
    [RelayCommand]
    private async Task TestBlowAsync()
    {
        if (!_dispatcher.Enabled)
        {
            StatusText = "⚠ 目前是停用狀態：請先勾「啟用」並按儲存，再測試。";
            return;
        }

        // 用時間戳當 id，確保每次測試都是新的一筆（不會被去重擋掉）。
        var req = new BlowRequest(
            Id: $"TEST-{DateTime.Now:HHmmssfff}",
            CreatedAt: DateTime.Now,
            Reason: BlowRequest.ReasonTest,
            ExpectedMohao: "TEST",
            ExpectedXuehao: "00",
            DetectedMohao: "TEST",
            DetectedXuehao: "00",
            ConfMohao: 1.0,
            ConfXuehao: 1.0,
            DelayMs: 0);

        // ⚠ 測試也走同一條佇列：這樣測到的延遲/通道/去重就是產線實際會走的路。
        var queued = _dispatcher.Enqueue(req);
        // ⚠ 訊息裡的通道名一定要**現場重讀**：這行字是使用者判斷「我現在送的是哪種格式」的依據，
        //    如果沿用建構式那次的舊值，勾了 JSON 卻寫「NG 純文字」，正好把紅字警告的防呆抵消掉
        //    （2026-08-24 UI 實測發現④）。
        OutputName = _output.DisplayName;
        // TEST 原因不受 MISMATCH／NG 開關限制，所以走到這裡沒排入通常是「還沒儲存」。
        StatusText = queued
            ? $"已送出測試吹氣（{DelayMs} ms 後動作）→ {OutputName}。看吹嘴有沒有反應；"
              + "沒反應就查對方 IO 監聽程式是否啟動、埠號是否一致、防火牆是否放行。"
            : "⚠ 沒有排入——設定可能還沒儲存（改完要先按「儲存」才會生效）。";
        await Task.CompletedTask;
    }

    private BlowOptions ToOptions() => new()
    {
        Enabled = Enabled,
        DelayMs = DelayMs,
        BlowOnMismatch = BlowOnMismatch,
        BlowOnNg = BlowOnNg,
        Host = Host,
        Port = Port,
        Channel = Channel,
        ConnectTimeoutMs = ConnectTimeoutMs,
        Output = UseLogOnly ? "Log" : "Tcp",
        Format = UseJsonFormat ? "Json" : "NgText",
        KeepAlive = KeepAlive,
    };

    /// <summary>把 Normalize 夾過的值寫回畫面，讓使用者看到真正生效的數字。</summary>
    private void ApplyBack(BlowOptions o)
    {
        Host = o.Host;
        Port = o.Port;
        Channel = o.Channel;
        DelayMs = o.DelayMs;
        ConnectTimeoutMs = o.ConnectTimeoutMs;

        // 「目前通道」必須跟著剛存下去的格式走。原本只在建構式設一次，
        // 於是勾了「送 JSON」存檔後畫面仍顯示「NG 純文字」——而那正是會讓對方不吹的格式。
        OutputName = _output.DisplayName;
    }

    private static string ResolveConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "configs", "blow.json");
}
