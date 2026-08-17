namespace AIVision.Application.Configuration;

/// <summary>
/// 瑕疵過濾配置選項
/// 用於控制瑕疵尺寸與距離的過濾規則
/// </summary>
public sealed class DefectFilteringOptions
{
    /// <summary>
    /// 配置區段名稱
    /// </summary>
    public const string SectionName = "DefectFiltering";

    /// <summary>
    /// 是否啟用瑕疵過濾規則
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 單一像素面積 (mm²)
    /// 預設值對應 2.4×2.4 µm² = 0.00000576 mm²
    /// </summary>
    public double PixelAreaMm2 { get; set; } = 0.00000576;

    /// <summary>
    /// 單一像素邊長 (mm)
    /// 預設值對應 2.4 µm = 0.0024 mm
    /// 用於計算瑕疵之間的距離
    /// </summary>
    public double PixelSizeMm { get; set; } = 0.0024;

    /// <summary>
    /// 最小檢出面積閾值 (mm²)
    /// 小於此值的瑕疵將被忽略
    /// </summary>
    public double MinimumAreaMm2 { get; set; } = 0.02;

    /// <summary>
    /// 中等/大瑕疵分界閾值 (mm²)
    /// 大於或等於此值的瑕疵直接判定為 NG
    /// </summary>
    public double MediumAreaMm2 { get; set; } = 0.05;

    /// <summary>
    /// 群聚距離閾值 (mm)
    /// 中等瑕疵之間距離小於此值時判定為 NG
    /// </summary>
    public double CloseDistanceMm { get; set; } = 50.0;

    /// <summary>
    /// 關鍵瑕疵類別列表
    /// 這些類別的瑕疵無論大小都直接判定為 NG
    /// </summary>
    public List<string> CriticalClasses { get; set; } = new();

    /// <summary>
    /// 將像素面積轉換為 mm²
    /// </summary>
    public double ConvertPixelAreaToMm2(int pixelArea)
    {
        return pixelArea * PixelAreaMm2;
    }

    /// <summary>
    /// 將像素距離轉換為 mm
    /// </summary>
    public double ConvertPixelDistanceToMm(double pixelDistance)
    {
        return pixelDistance * PixelSizeMm;
    }

    /// <summary>
    /// 取得群聚距離閾值（像素）
    /// </summary>
    public double GetCloseDistancePixels()
    {
        return CloseDistanceMm / PixelSizeMm;
    }
}
