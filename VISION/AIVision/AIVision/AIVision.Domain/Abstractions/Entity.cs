namespace AIVision.Domain.Abstractions;

/// <summary>
/// 抽象領域實體基底，確保唯一識別與封裝狀態行為。
/// </summary>
public abstract class Entity<TId>
{
    public TId Id { get; protected set; } = default!;
}
