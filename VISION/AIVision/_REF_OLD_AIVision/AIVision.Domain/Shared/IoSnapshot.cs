namespace AIVision.Domain.Shared;

/// <summary>
/// PLC I/O 讀取快照。
/// </summary>
public readonly record struct IoSnapshot(
    bool InReady,
    bool OutCapture,
    bool OutResult,
    int RawMask);
