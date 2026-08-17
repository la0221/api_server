using AIVision.Application.Configuration;
using AIVision.Application.Ports.Services;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 瑕疵過濾服務實作
/// 根據客戶定義的尺寸與距離規則過濾瑕疵
///
/// 規則說明：
/// 1. 關鍵瑕疵（CriticalClasses）：無論大小，直接判定 NG
/// 2. 大瑕疵（≥ MediumAreaMm2）：直接判定 NG
/// 3. 中等瑕疵（MinimumAreaMm2 ~ MediumAreaMm2）：
///    - 單一：OK
///    - 複數且距離 < CloseDistanceMm：NG
///    - 複數且距離 ≥ CloseDistanceMm：OK
/// 4. 小瑕疵（≤ MinimumAreaMm2）：忽略
/// </summary>
public sealed class DefectFilteringService : IDefectFilteringService
{
    private readonly ILogger<DefectFilteringService> _logger;
    private DefectFilteringOptions _options;

    public DefectFilteringService(
        ILogger<DefectFilteringService> logger,
        IOptions<DefectFilteringOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        LogConfiguration();
    }

    /// <summary>
    /// 記錄目前配置
    /// </summary>
    private void LogConfiguration()
    {
        if (_options.Enabled)
        {
            _logger.LogInformation("[DefectFilter] 瑕疵過濾已啟用");
            _logger.LogInformation("[DefectFilter] 配置: PixelArea={PixelArea:F8} mm²/px, MinArea={MinArea} mm², MediumArea={MediumArea} mm²",
                _options.PixelAreaMm2, _options.MinimumAreaMm2, _options.MediumAreaMm2);
            _logger.LogInformation("[DefectFilter] 群聚距離閾值: {Distance:F2} mm ({DistancePx:F0} px)",
                _options.CloseDistanceMm, _options.GetCloseDistancePixels());

            if (_options.CriticalClasses.Count > 0)
            {
                _logger.LogInformation("[DefectFilter] 關鍵瑕疵類別: {Classes}",
                    string.Join(", ", _options.CriticalClasses));
            }
        }
        else
        {
            _logger.LogInformation("[DefectFilter] 瑕疵過濾已停用");
        }
    }

    /// <inheritdoc />
    public DefectFilteringResult FilterDefects(IReadOnlyList<WorkflowDefect> defects)
    {
        // 若未啟用過濾，直接回傳原始結果
        if (!_options.Enabled)
        {
            var hasDefect = defects.Any(d => d.HasDefect);
            return new DefectFilteringResult
            {
                IsOk = !hasDefect,
                ValidDefects = defects.Where(d => d.HasDefect).ToList(),
                FilteredDefects = Array.Empty<WorkflowDefect>(),
                FilteringReason = "過濾功能未啟用"
            };
        }

        _logger.LogInformation("[DefectFilter] ========== 瑕疵過濾開始 ==========");
        _logger.LogInformation("[DefectFilter] 輸入瑕疵數量: {Count}", defects.Count);

        // 分類瑕疵
        var criticalDefects = new List<WorkflowDefect>();
        var largeDefects = new List<WorkflowDefect>();
        var mediumDefects = new List<WorkflowDefect>();
        var smallDefects = new List<WorkflowDefect>();

        int index = 1;
        foreach (var defect in defects)
        {
            if (!defect.HasDefect)
            {
                _logger.LogDebug("[DefectFilter] 瑕疵 #{Index}: Class={Class}, 無有效面積，跳過",
                    index, defect.ClassName);
                index++;
                continue;
            }

            var category = ClassifyDefect(defect);
            var areaMm2 = CalculateAreaMm2(defect.TotalArea);

            switch (category)
            {
                case DefectSizeCategory.Critical:
                    criticalDefects.Add(defect);
                    _logger.LogDebug("[DefectFilter] 瑕疵 #{Index}: Class={Class}, Area={Area} px ({AreaMm2:F6} mm²) → 關鍵瑕疵 ✗",
                        index, defect.ClassName, defect.TotalArea, areaMm2);
                    break;

                case DefectSizeCategory.Large:
                    largeDefects.Add(defect);
                    _logger.LogDebug("[DefectFilter] 瑕疵 #{Index}: Class={Class}, Area={Area} px ({AreaMm2:F6} mm²) → 大瑕疵 ✗",
                        index, defect.ClassName, defect.TotalArea, areaMm2);
                    break;

                case DefectSizeCategory.Medium:
                    mediumDefects.Add(defect);
                    _logger.LogDebug("[DefectFilter] 瑕疵 #{Index}: Class={Class}, Area={Area} px ({AreaMm2:F6} mm²) → 中等瑕疵",
                        index, defect.ClassName, defect.TotalArea, areaMm2);
                    break;

                case DefectSizeCategory.Small:
                    smallDefects.Add(defect);
                    _logger.LogDebug("[DefectFilter] 瑕疵 #{Index}: Class={Class}, Area={Area} px ({AreaMm2:F6} mm²) → 小瑕疵（忽略）",
                        index, defect.ClassName, defect.TotalArea, areaMm2);
                    break;
            }

            index++;
        }

        _logger.LogInformation("[DefectFilter] 分類結果: 關鍵={Critical}, 大={Large}, 中={Medium}, 小={Small}（過濾）",
            criticalDefects.Count, largeDefects.Count, mediumDefects.Count, smallDefects.Count);

        // 判定邏輯
        string reason;
        bool isOk;
        var validDefects = new List<WorkflowDefect>();

        // 1. 檢查關鍵瑕疵
        if (criticalDefects.Count > 0)
        {
            isOk = false;
            reason = $"發現 {criticalDefects.Count} 個關鍵瑕疵，直接判定 NG";
            validDefects.AddRange(criticalDefects);
            validDefects.AddRange(largeDefects);
            validDefects.AddRange(mediumDefects);
            _logger.LogInformation("[DefectFilter] ✗ {Reason}", reason);
        }
        // 2. 檢查大瑕疵
        else if (largeDefects.Count > 0)
        {
            isOk = false;
            reason = $"發現 {largeDefects.Count} 個大瑕疵（≥ {_options.MediumAreaMm2} mm²），直接判定 NG";
            validDefects.AddRange(largeDefects);
            validDefects.AddRange(mediumDefects);
            _logger.LogInformation("[DefectFilter] ✗ {Reason}", reason);
        }
        // 3. 檢查中等瑕疵
        else if (mediumDefects.Count >= 2)
        {
            // 檢查距離
            var (hasCloseDefects, minDistance, defect1Idx, defect2Idx) = CheckCloseDefects(mediumDefects);

            if (hasCloseDefects)
            {
                isOk = false;
                reason = $"發現 {mediumDefects.Count} 個中等瑕疵，最近距離 {minDistance:F2} mm < {_options.CloseDistanceMm} mm，判定 NG";
                validDefects.AddRange(mediumDefects);
                _logger.LogInformation("[DefectFilter] ✗ {Reason}", reason);
            }
            else
            {
                isOk = true;
                reason = $"發現 {mediumDefects.Count} 個中等瑕疵，但距離 {minDistance:F2} mm ≥ {_options.CloseDistanceMm} mm，判定 OK";
                _logger.LogInformation("[DefectFilter] ✓ {Reason}", reason);
            }
        }
        // 4. 單一中等瑕疵或無有效瑕疵
        else if (mediumDefects.Count == 1)
        {
            isOk = true;
            reason = "僅 1 個中等瑕疵，無須檢出，判定 OK";
            _logger.LogInformation("[DefectFilter] ✓ {Reason}", reason);
        }
        else
        {
            isOk = true;
            reason = smallDefects.Count > 0
                ? $"僅有 {smallDefects.Count} 個小瑕疵（已忽略），判定 OK"
                : "無有效瑕疵，判定 OK";
            _logger.LogInformation("[DefectFilter] ✓ {Reason}", reason);
        }

        _logger.LogInformation("[DefectFilter] ========== 瑕疵過濾結束 ==========");
        _logger.LogInformation("[DefectFilter] 結果: {Result}, 有效瑕疵: {Valid}, 過濾瑕疵: {Filtered}",
            isOk ? "OK" : "NG", validDefects.Count, smallDefects.Count);

        return new DefectFilteringResult
        {
            IsOk = isOk,
            ValidDefects = validDefects,
            FilteredDefects = smallDefects,
            FilteringReason = reason,
            CriticalDefectCount = criticalDefects.Count,
            LargeDefectCount = largeDefects.Count,
            MediumDefectCount = mediumDefects.Count,
            SmallDefectCount = smallDefects.Count
        };
    }

    /// <summary>
    /// 分類瑕疵
    /// </summary>
    private DefectSizeCategory ClassifyDefect(WorkflowDefect defect)
    {
        // 先檢查是否為關鍵瑕疵類別
        if (_options.CriticalClasses.Contains(defect.ClassName, StringComparer.OrdinalIgnoreCase))
        {
            return DefectSizeCategory.Critical;
        }

        var areaMm2 = CalculateAreaMm2(defect.TotalArea);

        if (areaMm2 >= _options.MediumAreaMm2)
        {
            return DefectSizeCategory.Large;
        }
        else if (areaMm2 > _options.MinimumAreaMm2)
        {
            return DefectSizeCategory.Medium;
        }
        else
        {
            return DefectSizeCategory.Small;
        }
    }

    /// <summary>
    /// 檢查中等瑕疵之間是否有距離過近的情況
    /// </summary>
    private (bool HasClose, double MinDistance, int Defect1Index, int Defect2Index) CheckCloseDefects(
        List<WorkflowDefect> mediumDefects)
    {
        if (mediumDefects.Count < 2)
            return (false, double.MaxValue, -1, -1);

        _logger.LogInformation("[DefectFilter] 檢查 {Count} 個中等瑕疵之間的距離...", mediumDefects.Count);

        double minDistance = double.MaxValue;
        int minIdx1 = -1, minIdx2 = -1;

        for (int i = 0; i < mediumDefects.Count - 1; i++)
        {
            for (int j = i + 1; j < mediumDefects.Count; j++)
            {
                var distance = CalculateDistanceMm(mediumDefects[i], mediumDefects[j]);

                _logger.LogDebug("[DefectFilter] 瑕疵 #{I} ↔ #{J}: 距離 = {Distance:F2} mm",
                    i + 1, j + 1, distance);

                if (distance < minDistance)
                {
                    minDistance = distance;
                    minIdx1 = i;
                    minIdx2 = j;
                }
            }
        }

        var hasClose = minDistance < _options.CloseDistanceMm;
        return (hasClose, minDistance, minIdx1, minIdx2);
    }

    /// <summary>
    /// 計算瑕疵質心
    /// </summary>
    private (double X, double Y) GetCentroid(WorkflowDefect defect)
    {
        if (defect.Contours == null || defect.Contours.Count == 0)
            return (0, 0);

        // 將所有輪廓的點合併計算質心
        var allPoints = defect.Contours.SelectMany(c => c).ToList();

        if (allPoints.Count == 0)
            return (0, 0);

        double avgX = allPoints.Average(p => (double)p.X);
        double avgY = allPoints.Average(p => (double)p.Y);

        return (avgX, avgY);
    }

    /// <inheritdoc />
    public void UpdateOptions(DefectFilteringOptions options)
    {
        _options = options;
        _logger.LogInformation("[DefectFilter] 配置已更新");
        LogConfiguration();
    }

    /// <inheritdoc />
    public DefectFilteringOptions GetCurrentOptions()
    {
        return _options;
    }

    /// <inheritdoc />
    public double CalculateAreaMm2(int pixelArea)
    {
        return _options.ConvertPixelAreaToMm2(pixelArea);
    }

    /// <inheritdoc />
    public double CalculateDistanceMm(WorkflowDefect defect1, WorkflowDefect defect2)
    {
        var (x1, y1) = GetCentroid(defect1);
        var (x2, y2) = GetCentroid(defect2);

        double dx = x2 - x1;
        double dy = y2 - y1;
        double distancePixels = Math.Sqrt(dx * dx + dy * dy);

        var distanceMm = _options.ConvertPixelDistanceToMm(distancePixels);

        _logger.LogDebug("[DefectFilter] 距離計算: ({X1:F1}, {Y1:F1}) ↔ ({X2:F1}, {Y2:F1}) = {DistPx:F1} px = {DistMm:F2} mm",
            x1, y1, x2, y2, distancePixels, distanceMm);

        return distanceMm;
    }
}
