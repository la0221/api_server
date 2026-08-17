namespace AIVision.Application.Contracts;

/// <summary>
/// 模型載入請求。
/// </summary>
/// <param name="ModelUuid">模型唯一識別碼（通常為 "model"）</param>
/// <param name="ModelPath">模型完整路徑（相對於 ModelBasePath）</param>
/// <param name="Port">模型服務埠號</param>
public sealed record ModelLoadRequest(
    string ModelUuid,
    string ModelPath,
    int Port);

