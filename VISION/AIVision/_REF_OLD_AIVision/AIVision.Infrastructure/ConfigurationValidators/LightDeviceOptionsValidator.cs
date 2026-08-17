using System.Net;
using AIVision.Infrastructure.Devices.Light;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.ConfigurationValidators;

/// <summary>
/// 光源控制器設定驗證器
/// </summary>
public sealed class LightDeviceOptionsValidator : IValidateOptions<LightDeviceOptions>
{
    public ValidateOptionsResult Validate(string? name, LightDeviceOptions options)
    {
        var errors = new List<string>();

        // 驗證監聽 IP
        if (string.IsNullOrWhiteSpace(options.ListenIp))
        {
            errors.Add("光源控制器監聽 IP 不可為空");
        }
        else if (options.ListenIp != "0.0.0.0" && !IPAddress.TryParse(options.ListenIp, out _))
        {
            errors.Add($"光源控制器監聽 IP 格式無效: {options.ListenIp}");
        }

        // 驗證 Port 範圍
        if (options.ListenPort < 1 || options.ListenPort > 65535)
        {
            errors.Add($"光源控制器 Port 超出有效範圍 (1-65535): {options.ListenPort}");
        }

        // 驗證通道數
        if (options.ChannelCount < 1 || options.ChannelCount > 16)
        {
            errors.Add($"光源通道數超出有效範圍 (1-16): {options.ChannelCount}");
        }

        // 驗證超時值
        if (options.TimeoutMs < 100)
        {
            errors.Add($"光源控制器超時值過小 (最小 100ms): {options.TimeoutMs}");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
