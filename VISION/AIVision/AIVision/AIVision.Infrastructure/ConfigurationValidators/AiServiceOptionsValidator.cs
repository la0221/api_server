using AIVision.Application.Configuration;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.ConfigurationValidators;

/// <summary>
/// AI 服務設定驗證器
/// </summary>
public sealed class AiServiceOptionsValidator : IValidateOptions<AiServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, AiServiceOptions options)
    {
        var errors = new List<string>();

        // 如果 Type 是 Http，才需要驗證 BaseUrl
        if (options.IsHttpEnabled)
        {
            // 驗證 BaseUrl
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                errors.Add("AI 服務 BaseUrl 不可為空 (Type=Http 時必須設定)");
            }
            else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri))
            {
                errors.Add($"AI 服務 BaseUrl 格式無效: {options.BaseUrl}");
            }
            else if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                errors.Add($"AI 服務 BaseUrl 必須使用 http 或 https: {options.BaseUrl}");
            }
        }

        // 驗證超時值
        if (options.TimeoutMs < 100)
        {
            errors.Add($"AI 服務超時值過小 (最小 100ms): {options.TimeoutMs}");
        }

        if (options.TimeoutMs > 300000)
        {
            errors.Add($"AI 服務超時值過大 (最大 300000ms): {options.TimeoutMs}");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
