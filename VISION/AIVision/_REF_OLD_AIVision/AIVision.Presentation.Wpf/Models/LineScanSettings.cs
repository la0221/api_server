using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVision.Presentation.Wpf.Models;

/// <summary>
/// Line Scan 使用者設定 (用於 JSON 儲存/載入)
/// 包含 ROI 參數、相機參數、儲存設定等使用者偏好設定
/// </summary>
public sealed class LineScanUserSettings
{
    // === ROI 參數 ===
    public long OffsetX { get; set; }
    public long OffsetY { get; set; }
    public long Width { get; set; } = 1200;
    public int TargetHeight { get; set; } = 2000;
    public double LineRate { get; set; } = 1000;

    // === 相機參數 ===
    public double ExposureTime { get; set; } = 10000;
    public double Gain { get; set; } = 1.0;

    // === 相機 UserSet 設定 ===
    /// <summary>
    /// 要載入的相機 UserSet 名稱 (UserSet0, UserSet1, 或 Linescan)
    /// </summary>
    public string UserSetName { get; set; } = "Linescan";

    // === 儲存設定 ===
    public bool AutoSaveImages { get; set; } = true;
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LineScan");

    // === 連續掃描設定 ===
    public bool ContinuousScan { get; set; }

    /// <summary>
    /// 設定檔案的預設路徑
    /// </summary>
    [JsonIgnore]
    public static string DefaultFilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "linescan-settings.json");

    /// <summary>
    /// 從 JSON 檔案載入設定
    /// </summary>
    public static LineScanUserSettings? Load(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LineScanUserSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 儲存設定到 JSON 檔案
    /// </summary>
    public bool Save(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
