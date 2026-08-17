namespace AIVision.Application.Interfaces;

/// <summary>
/// 健康檢查介面
/// </summary>
public interface IHealthCheck
{
    /// <summary>檢查名稱</summary>
    string Name { get; }

    /// <summary>執行健康檢查</summary>
    Task<HealthCheckResult> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// 健康檢查結果
/// </summary>
public record HealthCheckResult(
    bool IsHealthy,
    string? Message = null,
    Exception? Exception = null);
