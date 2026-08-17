namespace AIVision.Application.Services;

using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;
using Microsoft.Extensions.Logging;

/// <summary>
/// 工單管理服務實作
/// </summary>
public sealed class WorkOrderManagementService : IWorkOrderManagementService
{
    private readonly IWorkOrderRepository _repository;
    private readonly IInspectionImageService _imageService;
    private readonly ILogger<WorkOrderManagementService> _logger;
    private WorkOrder? _currentWorkOrder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WorkOrderManagementService(
        IWorkOrderRepository repository,
        IInspectionImageService imageService,
        ILogger<WorkOrderManagementService> logger)
    {
        _repository = repository;
        _imageService = imageService;
        _logger = logger;
    }

    public async Task<WorkOrder?> GetCurrentWorkOrderAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _currentWorkOrder;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkOrder> CreateWorkOrderAsync(
        string productName,
        string? modelName,
        string? machineModelName,
        CancellationToken cancellationToken)
    {
        return await CreateWorkOrderAsync(productName, modelName, machineModelName, null, cancellationToken);
    }

    public async Task<WorkOrder> CreateWorkOrderAsync(
        string productName,
        string? modelName,
        string? machineModelName,
        string? customWorkOrderCode,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("開始建立工單: Product={ProductName}, Model={ModelName}, CustomCode={CustomCode}",
            productName, modelName, customWorkOrderCode);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // 如果有活動工單，先結束它
            if (_currentWorkOrder != null && !_currentWorkOrder.IsClosed)
            {
                _logger.LogInformation("關閉前一個工單: Code={Code}", _currentWorkOrder.Code);
                _currentWorkOrder.Close();
                await _repository.UpdateAsync(_currentWorkOrder, cancellationToken);
            }

            // 使用自訂工單號或自動生成
            var code = string.IsNullOrWhiteSpace(customWorkOrderCode)
                ? await GenerateUniqueWorkOrderCodeAsync(cancellationToken)
                : customWorkOrderCode.Trim();

            var workOrder = new WorkOrder(
                code: code,
                productName: productName,
                modelName: modelName,
                machineModelName: machineModelName);

            await _repository.AddAsync(workOrder, cancellationToken);
            _currentWorkOrder = workOrder;

            // 創建工單圖片資料夾
            try
            {
                _imageService.EnsureWorkOrderFoldersExist(workOrder.Code);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "創建工單圖片資料夾失敗，但不影響工單創建");
            }

            _logger.LogInformation("工單建立成功: Code={Code}, Product={ProductName}, Model={ModelName}",
                code, productName, modelName);

            return workOrder;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立工單失敗: Product={ProductName}", productName);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EndCurrentWorkOrderAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentWorkOrder == null || _currentWorkOrder.IsClosed)
            {
                _logger.LogDebug("無活動工單需要結束");
                return;
            }

            var code = _currentWorkOrder.Code;
            _currentWorkOrder.Close();
            await _repository.UpdateAsync(_currentWorkOrder, cancellationToken);
            _currentWorkOrder = null;

            _logger.LogInformation("工單已結束: Code={Code}", code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "結束工單失敗: Code={Code}", _currentWorkOrder?.Code);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SwitchToWorkOrderAsync(
        string workOrderCode,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var workOrder = await _repository.GetByCodeAsync(workOrderCode, cancellationToken);
            if (workOrder == null)
            {
                throw new InvalidOperationException($"工單 {workOrderCode} 不存在");
            }

            // 結束當前工單
            if (_currentWorkOrder != null && !_currentWorkOrder.IsClosed)
            {
                _currentWorkOrder.Close();
                await _repository.UpdateAsync(_currentWorkOrder, cancellationToken);
            }

            // 切換到指定工單（重新激活）
            if (workOrder.IsClosed)
            {
                workOrder.UpdateStatus("Active");
                await _repository.UpdateAsync(workOrder, cancellationToken);
            }

            _currentWorkOrder = workOrder;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> GenerateUniqueWorkOrderCodeAsync(CancellationToken cancellationToken)
    {
        // 格式: WO-YYYYMMDD-HHmmss-fff (加上毫秒)
        var now = DateTime.Now;
        var baseCode = $"WO-{now:yyyyMMdd-HHmmss-fff}";

        // 檢查是否重複，如果重複就加上序號
        var code = baseCode;
        var counter = 1;

        while (await _repository.GetByCodeAsync(code, cancellationToken) != null)
        {
            code = $"{baseCode}-{counter}";
            counter++;
        }

        return code;
    }
}
