namespace AIVision.Infrastructure.Common;

/// <summary>
/// 指數退避策略
/// </summary>
public class ExponentialBackoff
{
    private readonly int _initialDelayMs;
    private readonly int _maxDelayMs;
    private readonly double _multiplier;
    private int _currentAttempt;

    public ExponentialBackoff(
        int initialDelayMs = 100,
        int maxDelayMs = 5000,
        double multiplier = 2.0)
    {
        _initialDelayMs = initialDelayMs;
        _maxDelayMs = maxDelayMs;
        _multiplier = multiplier;
        _currentAttempt = 0;
    }

    /// <summary>
    /// 取得下一次延遲時間 (ms)
    /// </summary>
    public int GetNextDelay()
    {
        var delay = (int)(_initialDelayMs * Math.Pow(_multiplier, _currentAttempt));
        delay = Math.Min(delay, _maxDelayMs);
        _currentAttempt++;
        return delay;
    }

    /// <summary>
    /// 重置退避計數器
    /// </summary>
    public void Reset() => _currentAttempt = 0;

    /// <summary>
    /// 目前重試次數
    /// </summary>
    public int CurrentAttempt => _currentAttempt;
}
