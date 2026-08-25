using System;
using System.IO;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 實時模號穴號檢測設定（appsettings <c>RealtimeInspection</c> 段）。
/// 現場要調的東西一律放這裡，不 hardcode。
/// </summary>
public sealed class RealtimeInspectionOptions
{
    public const string SectionName = "RealtimeInspection";

    /// <summary>站號。會出現在 pieceId、送父端的欄位、父端的最近辨識紀錄。</summary>
    public string StationId { get; set; } = "ST-01";

    /// <summary>
    /// 擷取窗（ms）：觸發後在這段時間內回頭找「完整鏡片正常進框」的那一幀。
    /// 相機版實證值 800ms。太短會把慢進框的片子誤判成擷取失誤；太長會拖慢節拍。
    /// </summary>
    public int CaptureWindowMs { get; set; } = 800;

    /// <summary>
    /// **父端回應預算（ms）**。超過就改用本機模型接管，不等。
    /// <para>2026-08-24 拍板 100ms。⚠ 當日實測父端自報耗時 42–194ms（多筆落在 92–100），
    /// 這個值會讓相當比例的片子落到本機接管（本機是較舊的雙 head）。
    /// 所以做成可設定 —— 現場跑一輪看「中央/本機」比例，不夠就往上調，不必改程式。</para>
    /// </summary>
    public int ServerBudgetMs { get; set; } = 100;

    /// <summary>父端連不上／5xx 後的降級冷卻（ms）。期間直接走本機，不再每片空等。</summary>
    public int ServerDownCooldownMs { get; set; } = 30_000;

    /// <summary>紀錄根目錄。留空＝<c>&lt;程式目錄&gt;\records</c>。</summary>
    public string? RecordRoot { get; set; }

    /// <summary>要不要存原圖。關掉可省一半容量（相機版只存一張）。</summary>
    public bool SaveRawImage { get; set; } = true;

    /// <summary>要不要存前處理圖（實際送父端那張）。</summary>
    public bool SaveStripImage { get; set; } = true;

    /// <summary>模號軸混料門檻（與 MoldCodePairCycle 對齊）。</summary>
    public double MoldThreshold { get; set; } = 0.60;

    /// <summary>穴號軸混料門檻。</summary>
    public double CavityThreshold { get; set; } = 0.85;

    /// <summary>模號 head 的不良品類名。</summary>
    public string NgClassName { get; set; } = "NG";

    /// <summary>
    /// 閘門的邊界容許值（半徑的比例）。0.10 = 允許鏡片超出邊界 10% 的半徑。
    /// <para>設太嚴會把本來讀得到的片子誤判成擷取失誤（正式前處理本身就容忍輕微超邊）；
    /// 設太鬆則「工件還沒完全進框」的幀會被放行、讀出缺字的值。</para>
    /// </summary>
    public double LensEdgeTolerance { get; set; } = 0.10;

    /// <summary>畫面上「最近幾片」保留筆數。</summary>
    public int RecentCapacity { get; set; } = 200;

    public string ResolvedRecordRoot =>
        string.IsNullOrWhiteSpace(RecordRoot)
            ? Path.Combine(AppContext.BaseDirectory, "records")
            : (Path.IsPathRooted(RecordRoot)
                ? RecordRoot
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RecordRoot)));

    /// <summary>把使用者填的值夾到合理範圍（畫面/設定檔可亂填，這裡是最後一道）。</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(StationId)) StationId = "ST-01";
        StationId = StationId.Trim();
        CaptureWindowMs = Math.Clamp(CaptureWindowMs, 100, 10_000);
        ServerBudgetMs = Math.Clamp(ServerBudgetMs, 20, 10_000);
        ServerDownCooldownMs = Math.Clamp(ServerDownCooldownMs, 0, 600_000);
        MoldThreshold = Math.Clamp(MoldThreshold, 0, 1);
        CavityThreshold = Math.Clamp(CavityThreshold, 0, 1);
        RecentCapacity = Math.Clamp(RecentCapacity, 10, 5000);
        LensEdgeTolerance = Math.Clamp(LensEdgeTolerance, 0.0, 0.5);
        if (string.IsNullOrWhiteSpace(NgClassName)) NgClassName = "NG";
    }
}
