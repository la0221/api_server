using System.Collections.Generic;

namespace AIVision.Infrastructure.Devices.Camera.Ids;

public sealed class IdsCameraOptions
{
    public const string SectionName = "Devices:Camera";

    /// <summary>
    /// 驅動類型標識，預期為 IdsPeak。
    /// </summary>
    public string Type { get; set; } = "IdsPeak";

    /// <summary>
    /// 優先嘗試開啟的相機序號或路徑。
    /// </summary>
    public List<string> Preferred { get; set; } = new();

    /// <summary>
    /// IDS peak SDK 資源所在資料夾，相對於應用程式根目錄。
    /// </summary>
    public string SdkPath { get; set; } = "libs/cameras/IDS_PEAK";

    /// <summary>
    /// 參數設定檔路徑（JSON），相對於應用程式根目錄。
    /// </summary>
    public string ConfigPath { get; set; } = "configs/camera-ids.json";

    /// <summary>
    /// **IO 觸發線的線號**（現場的觸發開關接在相機的數位輸入上，不是接 PLC）。
    /// <para>已驗證的「模號檢驗/相機版」用的是 <c>Line0</c>。這條線讀不到的話，
    /// 實時檢測只能靠畫面上的手動按鈕，等於沒有觸發源。</para>
    /// </summary>
    public string TriggerLine { get; set; } = "Line0";

    /// <summary>
    /// 目標 ROI 高度（像素）。
    /// </summary>
    public long Height { get; set; } = 1024;

    /// <summary>
    /// 目標曝光時間（微秒）。
    /// </summary>
    public double ExposureTimeUs { get; set; } = 15000;

    /// <summary>
    /// 目標增益 selector。
    /// </summary>
    public string GainSelector { get; set; } = "AnalogAll";

    /// <summary>
    /// 目標增益值。
    /// </summary>
    public double Gain { get; set; } = 2.0;

    /// <summary>
    /// 線掃相機專用目標線頻（Hz），面陣相機可為 null。
    /// </summary>
    public double? AcquisitionLineRate { get; set; }
}
