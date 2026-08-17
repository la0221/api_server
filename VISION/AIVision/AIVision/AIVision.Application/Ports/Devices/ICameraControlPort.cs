using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// 定義可調整相機內部參數的控制埠。
/// </summary>
public interface ICameraControlPort
{
    /// <summary>
    /// 取得目前支援的參數清單與數值範圍。
    /// </summary>
    Task<IReadOnlyList<CameraParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 更新單一參數的目標值。
    /// </summary>
    Task SetParameterAsync(CameraParameterKind kind, double value, CancellationToken cancellationToken);
}

/// <summary>
/// 可調整之相機參數種類。
/// </summary>
public enum CameraParameterKind
{
    ExposureTime,
    Gain,
    Height,
    AcquisitionLineRate,
    /// <summary>ROI 水平偏移 (Line Scan 用)</summary>
    OffsetX,
    /// <summary>ROI 垂直偏移 (Line Scan 用)</summary>
    OffsetY,
    /// <summary>ROI 寬度 (Line Scan 用)</summary>
    Width
}

/// <summary>
/// 相機參數描述，包含範圍、步階與目前值。
/// </summary>
/// <param name="Kind">參數種類。</param>
/// <param name="DisplayName">顯示名稱。</param>
/// <param name="Value">目前值。</param>
/// <param name="Minimum">最小值。</param>
/// <param name="Maximum">最大值。</param>
/// <param name="Step">步進。</param>
/// <param name="IsIntegral">是否為整數參數。</param>
public sealed record CameraParameterDescriptor(
    CameraParameterKind Kind,
    string DisplayName,
    double Value,
    double Minimum,
    double Maximum,
    double Step,
    bool IsIntegral);
