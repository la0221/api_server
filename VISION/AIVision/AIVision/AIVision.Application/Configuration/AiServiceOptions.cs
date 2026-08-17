using System;
using System.ComponentModel.DataAnnotations;

namespace AIVision.Application.Configuration;

public sealed class AiServiceOptions
{
    /// <summary>
    /// Adapter type. Use "Http" for remote service; any other value falls back to fake implementation.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Service base URL, e.g. https://ai-gateway.internal
    /// </summary>
    [Url]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Relative path for prediction endpoint. Defaults to /v1/inference.
    /// </summary>
    [Required]
    public string PredictPath { get; set; } = "/v1/inference";

    /// <summary>
    /// Optional API key for authorization header.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Header name used for API key, default Authorization.
    /// </summary>
    [Required]
    public string ApiKeyHeader { get; set; } = "Authorization";

    /// <summary>
    /// Prefix appended before API key value, e.g. "Bearer ".
    /// </summary>
    public string ApiKeyPrefix { get; set; } = "Bearer ";

    /// <summary>
    /// Optional default model name to send with request.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// HTTP request timeout in milliseconds.
    /// </summary>
    [Range(100, 60000)]
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Maximum retry attempts for transient failures.
    /// </summary>
    [Range(0, 5)]
    public int RetryCount { get; set; } = 2;

    /// <summary>
    /// Optional health check endpoint path. Defaults to "/health" or "/" if not specified.
    /// </summary>
    public string? HealthCheckPath { get; set; }

    /// <summary>
    /// Returns true when Type is Http (case-insensitive) and BaseUrl is provided.
    /// </summary>
    public bool IsHttpEnabled =>
        string.Equals(Type, "Http", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(BaseUrl);
}
