using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AIVision.MoldCode.Onnx;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// 判定區 ROI（送去辨識的那塊範圍）。
///
/// <para><b>為什麼存「比例」不存像素</b>（沿用已驗證的 <c>模號檢驗/相機版</c>）：
/// 像素 ROI 綁死解析度 —— 換相機、改 Binning、改 Width/Height，那組數字就整個錯位，
/// 而且錯了畫面上看不出來（會裁到旁邊，讀值全錯但一切「正常」）。
/// 存 0~1 的比例則與解析度、與畫面縮放都無關，要用時再乘回當下的幀尺寸。</para>
///
/// <para><b>為什麼放 <c>configs/roi.json</c> 而不是 appsettings</b>：ROI 是**現場校正值**，
/// 要在機台上邊看邊拖曳調整、存檔即生效；appsettings 是部署設定，改它還要重啟、
/// 而且 rebuild 會被蓋掉。同 <c>configs/blow.json</c>、<c>configs/camera-ids.json</c> 的作法。</para>
///
/// <para><b>第一次執行會自動搬家</b>：<c>configs/roi.json</c> 不存在時，
/// 用 appsettings 既有的像素 ROI 換算成比例並存檔（見 <see cref="SeedFromPixels"/>）——
/// 現場不必手動重設，也不會突然變成「沒有 ROI」。</para>
/// </summary>
public sealed class RoiSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly ILogger<RoiSettings>? _logger;
    private readonly object _gate = new();

    public RoiSettings(ILogger<RoiSettings>? logger = null) => _logger = logger;

    /// <summary>設定檔位置。</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "configs", "roi.json");

    // 預設值＝相機版現場校正後的比例（OCR_demo config.IDS_ROI 換算）
    public double Fx { get; private set; } = 0.2266;
    public double Fy { get; private set; }
    public double Fw { get; private set; } = 0.5703;
    public double Fh { get; private set; } = 0.5957;

    /// <summary>是否已從檔案載入（false＝目前用的是預設或 appsettings 換算來的）。</summary>
    public bool LoadedFromFile { get; private set; }

    /// <summary>ROI 變更時通知（畫面重畫、管線換參數）。</summary>
    public event EventHandler? Changed;

    /// <summary>比例 → 指定幀尺寸的像素矩形，並夾在畫面內。辨識、閘門、存圖全都用它。</summary>
    public (int X, int Y, int W, int H) ToPixels(int frameW, int frameH)
    {
        lock (_gate)
        {
            int x = (int)Math.Round(Fx * frameW);
            int y = (int)Math.Round(Fy * frameH);
            int w = (int)Math.Round(Fw * frameW);
            int h = (int)Math.Round(Fh * frameH);
            x = Math.Clamp(x, 0, Math.Max(0, frameW - 1));
            y = Math.Clamp(y, 0, Math.Max(0, frameH - 1));
            w = Math.Clamp(w, 1, frameW - x);
            h = Math.Clamp(h, 1, frameH - y);
            return (x, y, w, h);
        }
    }

    /// <summary>把目前 ROI 套進前處理參數（辨識器與閘門共用同一份，不會兩套漂移）。</summary>
    public void ApplyTo(WarpPolarParams p, int frameW, int frameH)
    {
        if (p is null || frameW <= 0 || frameH <= 0) return;
        var (x, y, w, h) = ToPixels(frameW, frameH);
        p.RoiX = x; p.RoiY = y; p.RoiW = w; p.RoiH = h;
    }

    /// <summary>載入 <c>configs/roi.json</c>；沒有檔案就維持現值並回 false。</summary>
    public bool Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var dto = JsonSerializer.Deserialize<RoiFile>(File.ReadAllText(ConfigPath, Encoding.UTF8));
            if (dto is null) return false;
            lock (_gate)
            {
                Fx = Clamp01(dto.Fx); Fy = Clamp01(dto.Fy);
                Fw = Math.Clamp(dto.Fw, 0.01, 1.0); Fh = Math.Clamp(dto.Fh, 0.01, 1.0);
                LoadedFromFile = true;
            }
            _logger?.LogInformation("[ROI] 已載入 {Path}：比例 {Fx:0.000},{Fy:0.000},{Fw:0.000},{Fh:0.000}",
                ConfigPath, Fx, Fy, Fw, Fh);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ROI] 載入失敗，改用預設值：{Path}", ConfigPath);
            return false;
        }
    }

    /// <summary>
    /// 從 appsettings 既有的**像素** ROI 換算成比例（第一次執行的搬家路徑）。
    /// <paramref name="frameW"/>/<paramref name="frameH"/> 要給那組像素值當初對應的幀尺寸。
    /// 已經有 roi.json 時不做事——檔案才是現場的真相。
    /// </summary>
    public bool SeedFromPixels(WarpPolarParams p, int frameW, int frameH)
    {
        if (LoadedFromFile || p is null) return false;
        if (p.RoiW <= 0 || p.RoiH <= 0 || frameW <= 0 || frameH <= 0) return false;

        lock (_gate)
        {
            Fx = Clamp01((double)p.RoiX / frameW);
            Fy = Clamp01((double)p.RoiY / frameH);
            Fw = Math.Clamp((double)p.RoiW / frameW, 0.01, 1.0);
            Fh = Math.Clamp((double)p.RoiH / frameH, 0.01, 1.0);
        }
        _logger?.LogInformation(
            "[ROI] 由 appsettings 的像素 ROI({X},{Y},{W},{H}) @ {FW}x{FH} 換算成比例並存檔",
            p.RoiX, p.RoiY, p.RoiW, p.RoiH, frameW, frameH);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>用畫面上框選的**像素**矩形更新（會換算成比例存檔）。</summary>
    public void SetFromPixels(int x, int y, int w, int h, int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0 || w <= 0 || h <= 0) return;
        lock (_gate)
        {
            Fx = Clamp01((double)x / frameW);
            Fy = Clamp01((double)y / frameH);
            Fw = Math.Clamp((double)w / frameW, 0.01, 1.0);
            Fh = Math.Clamp((double)h / frameH, 0.01, 1.0);
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重設回預設比例。</summary>
    public void Reset()
    {
        lock (_gate) { Fx = 0.2266; Fy = 0.0; Fw = 0.5703; Fh = 0.5957; }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>存檔。失敗只記 log —— ROI 存不進去不該擋住產線。</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RoiFile dto;
            lock (_gate) dto = new RoiFile { Fx = Fx, Fy = Fy, Fw = Fw, Fh = Fh };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(dto, JsonOpts), Encoding.UTF8);
            LoadedFromFile = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ROI] 存檔失敗（不影響目前這次執行）：{Path}", ConfigPath);
        }
    }

    /// <summary>給畫面看的一行字。</summary>
    public string Describe(int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0)
            return $"ROI 比例 {Fx:0.000}, {Fy:0.000}, {Fw:0.000}, {Fh:0.000}";
        var (x, y, w, h) = ToPixels(frameW, frameH);
        return $"ROI {x}, {y}, {w}×{h} px（比例 {Fx:0.000}, {Fy:0.000}, {Fw:0.000}, {Fh:0.000}）";
    }

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;

    private sealed class RoiFile
    {
        public double Fx { get; set; }
        public double Fy { get; set; }
        public double Fw { get; set; }
        public double Fh { get; set; }
    }
}
