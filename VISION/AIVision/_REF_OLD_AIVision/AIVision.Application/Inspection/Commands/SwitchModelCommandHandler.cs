using AIVision.Application.Configuration;
using AIVision.Application.Contracts;
using AIVision.Application.Ports.Devices;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Application.Inspection.Commands;

/// <summary>
/// 切換 AI 模型命令處理器。
/// </summary>
public sealed class SwitchModelCommandHandler : IRequestHandler<SwitchModelCommand, ModelLoadResult>
{
    private readonly IAiModelPort _modelPort;
    private readonly AinaviOptions _options;
    private readonly ILogger<SwitchModelCommandHandler> _logger;

    public SwitchModelCommandHandler(
        IAiModelPort modelPort,
        IOptions<AinaviOptions> options,
        ILogger<SwitchModelCommandHandler> logger)
    {
        _modelPort = modelPort;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ModelLoadResult> Handle(SwitchModelCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "開始切換模型: ModelName={ModelName}, Port={Port}",
                request.ModelName, request.Port);

            var modelRequest = new ModelLoadRequest(
                ModelUuid: "model",
                ModelPath: _options.GetModelPath(request.ModelName),
                Port: request.Port);

            var result = await _modelPort.LoadAsync(modelRequest, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "模型切換成功: {ModelName} on Port {Port}",
                    request.ModelName, request.Port);
            }
            else
            {
                _logger.LogWarning(
                    "模型切換失敗: {ModelName} on Port {Port}, Message: {Message}",
                    request.ModelName, request.Port, result.Message);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切換模型時發生異常: {ModelName}", request.ModelName);
            return new ModelLoadResult(false, $"Exception: {ex.Message}");
        }
    }
}

