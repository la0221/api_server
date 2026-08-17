namespace AIVision.Domain.Shared;

/// <summary>
/// PLC 輸出命令，避免直接操作旗標。
/// </summary>
public readonly struct IoCommand
{
    public bool CaptureStart { get; init; }
    public bool ResultOn { get; init; }

    /// <summary>混料剔除氣吹點位(模號核對 MixedAlarm 時觸發)。</summary>
    public bool AirBlow { get; init; }

    public static IoCommand CaptureStartCommand() => new() { CaptureStart = true };

    public static IoCommand Result(bool ok) => new() { ResultOn = ok };

    /// <summary>觸發氣吹剔除(混料件)。</summary>
    public static IoCommand Blow() => new() { AirBlow = true };
}
