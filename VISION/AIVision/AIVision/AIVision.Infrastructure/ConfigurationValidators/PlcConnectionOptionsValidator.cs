using System.Net;
using AIVision.Infrastructure.Devices.Plc.Communication;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.ConfigurationValidators;

/// <summary>
/// PLC 連線設定驗證器
/// </summary>
public sealed class PlcConnectionOptionsValidator : IValidateOptions<PlcConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, PlcConnectionOptions options)
    {
        var errors = new List<string>();

        // 驗證 IP 格式
        if (string.IsNullOrWhiteSpace(options.Ip))
        {
            errors.Add("PLC IP 地址不可為空");
        }
        else if (!IPAddress.TryParse(options.Ip, out _))
        {
            errors.Add($"PLC IP 地址格式無效: {options.Ip}");
        }

        // 驗證 Port 範圍
        if (options.Port < 1 || options.Port > 65535)
        {
            errors.Add($"PLC Port 超出有效範圍 (1-65535): {options.Port}");
        }

        // 驗證 UnitId 範圍
        if (options.UnitId < 0 || options.UnitId > 255)
        {
            errors.Add($"PLC UnitId 超出有效範圍 (0-255): {options.UnitId}");
        }

        // 驗證超時值
        if (options.ReadTimeoutMs < 100)
        {
            errors.Add($"讀取超時值過小 (最小 100ms): {options.ReadTimeoutMs}");
        }

        if (options.WriteTimeoutMs < 100)
        {
            errors.Add($"寫入超時值過小 (最小 100ms): {options.WriteTimeoutMs}");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
