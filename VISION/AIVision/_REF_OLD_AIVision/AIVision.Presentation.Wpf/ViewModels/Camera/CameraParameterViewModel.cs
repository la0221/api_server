using System;
using AIVision.Application.Ports.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIVision.Presentation.Wpf.ViewModels;

public sealed partial class CameraParameterViewModel : ObservableObject
{
    private readonly CameraViewModel _owner;
    private bool _suppress;

    public CameraParameterViewModel(CameraViewModel owner, CameraParameterDescriptor descriptor)
    {
        _owner = owner;
        Kind = descriptor.Kind;
        DisplayName = descriptor.DisplayName;
        Minimum = descriptor.Minimum;
        Maximum = descriptor.Maximum;
        Step = descriptor.Step;
        IsIntegral = descriptor.IsIntegral;

        _suppress = true;
        Value = descriptor.Value;
        _suppress = false;
        OnPropertyChanged(nameof(ValueString));
    }

    public CameraParameterKind Kind { get; }

    public string DisplayName { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public bool IsIntegral { get; }

    public bool SnapToTicks => Step > 0.000001;

    public double SmallChange => Step > 0.000001 ? Step : (IsIntegral ? 1 : 0.1);

    public string Tooltip =>
        Kind switch
        {
            CameraParameterKind.ExposureTime => "曝光時間，以微秒為單位。",
            CameraParameterKind.Gain => "增益值，通常需先關閉 GainAuto。",
            CameraParameterKind.Height => "ROI 高度，調整時請停止擷取並重新配置緩衝。",
            CameraParameterKind.AcquisitionLineRate => "線掃頻率，0 表示不啟用。",
            _ => string.Empty
        };

    public string ValueString => IsIntegral ? Value.ToString("0") : Value.ToString("0.##");

    [ObservableProperty]
    private double value;

    partial void OnValueChanged(double value)
    {
        if (_suppress)
        {
            return;
        }

        var normalized = Normalize(value);
        if (!AreClose(value, normalized))
        {
            try
            {
                _suppress = true;
                Value = normalized;
            }
            finally
            {
                _suppress = false;
            }

            return;
        }

        _ = _owner.ApplyParameterAsync(this, normalized);
        OnPropertyChanged(nameof(ValueString));
    }

    internal void UpdateValue(double newValue)
    {
        try
        {
            _suppress = true;
            Value = newValue;
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(ValueString));
    }

    private double Normalize(double value)
    {
        if (IsIntegral)
        {
            value = Math.Round(value);
        }

        if (Step > 0)
        {
            var steps = Math.Round((value - Minimum) / Step);
            value = Minimum + steps * Step;
        }

        return Math.Clamp(value, Minimum, Maximum);
    }

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 0.0001;
}
