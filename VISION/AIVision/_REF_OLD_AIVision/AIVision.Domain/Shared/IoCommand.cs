namespace AIVision.Domain.Shared;

/// <summary>
/// PLC 輸出命令，避免直接操作旗標。
/// </summary>
public readonly struct IoCommand
{
    public bool CaptureStart { get; init; }
    public bool ResultOn { get; init; }

    public static IoCommand CaptureStartCommand() => new() { CaptureStart = true };

    public static IoCommand Result(bool ok) => new() { ResultOn = ok };
}
