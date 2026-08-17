using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// 可切換的 AI 推論 Port，支援在運行時動態切換單一模型和 Workflow 模式
/// </summary>
public sealed class SwitchableAiInferencePort : IAiInferencePort
{
    private readonly AinaviAiInferencePort _singleModelPort;
    private readonly WorkflowAiInferencePort _workflowPort;
    private readonly ILogger<SwitchableAiInferencePort> _logger;

    private InferenceType _currentType = InferenceType.SingleModel;
    private readonly object _lock = new();

    public SwitchableAiInferencePort(
        AinaviAiInferencePort singleModelPort,
        WorkflowAiInferencePort workflowPort,
        ILogger<SwitchableAiInferencePort> logger)
    {
        _singleModelPort = singleModelPort;
        _workflowPort = workflowPort;
        _logger = logger;
    }

    /// <summary>
    /// 目前的推論類型
    /// </summary>
    public InferenceType CurrentType
    {
        get { lock (_lock) return _currentType; }
    }

    /// <summary>
    /// 是否已連線（根據目前模式回傳對應 Port 的狀態）
    /// </summary>
    public bool IsConnected => CurrentType switch
    {
        InferenceType.Workflow => _workflowPort.IsConnected,
        _ => _singleModelPort.IsConnected
    };

    /// <summary>
    /// 切換推論模式
    /// </summary>
    /// <param name="type">目標推論類型</param>
    public void SwitchTo(InferenceType type)
    {
        lock (_lock)
        {
            if (_currentType != type)
            {
                _logger.LogInformation("[SwitchableInference] 切換推論模式: {From} -> {To}",
                    _currentType, type);
                _currentType = type;
            }
        }
    }

    /// <summary>
    /// 執行 AI 推論（自動根據目前模式選擇對應的 Port）
    /// </summary>
    public async Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken)
    {
        var type = CurrentType;

        _logger.LogDebug("[SwitchableInference] 使用 {Type} 模式推論", type);

        return type switch
        {
            InferenceType.Workflow => await _workflowPort.PredictAsync(image, cancellationToken),
            _ => await _singleModelPort.PredictAsync(image, cancellationToken)
        };
    }

    /// <summary>
    /// 健康檢查（根據目前模式檢查對應的 Port）
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var type = CurrentType;

        return type switch
        {
            InferenceType.Workflow => await _workflowPort.HealthCheckAsync(cancellationToken),
            _ => await _singleModelPort.HealthCheckAsync(cancellationToken)
        };
    }

    /// <summary>
    /// 取得單一模型 Port（用於直接操作）
    /// </summary>
    public AinaviAiInferencePort SingleModelPort => _singleModelPort;

    /// <summary>
    /// 取得 Workflow Port（用於直接操作）
    /// </summary>
    public WorkflowAiInferencePort WorkflowPort => _workflowPort;
}
