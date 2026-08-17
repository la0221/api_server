using System.Text.Json;
using AIVision.Application.Contracts;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.AiService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AIVision.Application.Configuration;

namespace AIVision.Api.Controllers;

/// <summary>
/// AINAVI EdgeHub AI 推論 API 控制器。
/// </summary>
[ApiController]
[Route("api/ainavi")]
public sealed class AinaviController : ControllerBase
{
    private readonly IAiModelPort _modelPort;
    private readonly IAiInferencePort _inferencePort;
    private readonly IInferenceLogService _logService;
    private readonly AinaviOptions _options;
    private readonly ILogger<AinaviController> _logger;

    public AinaviController(
        IAiModelPort modelPort,
        IAiInferencePort inferencePort,
        IInferenceLogService logService,
        IOptions<AinaviOptions> options,
        ILogger<AinaviController> logger)
    {
        _modelPort = modelPort;
        _inferencePort = inferencePort;
        _logService = logService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 開啟/載入 AI 模型。
    /// </summary>
    /// <param name="input">模型載入請求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>載入結果</returns>
    [HttpPost("open-model")]
    [ProducesResponseType(typeof(ModelLoadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> OpenModel(
        [FromBody] OpenModelInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new ModelLoadRequest(
                ModelUuid: "model",
                ModelPath: _options.GetModelPath(input.ModelName),
                Port: input.Port);

            var result = await _modelPort.LoadAsync(request, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "模型開啟成功: {ModelName} on Port {Port}",
                    input.ModelName, input.Port);
                
                return Ok(result);
            }
            else
            {
                _logger.LogWarning(
                    "模型開啟失敗: {ModelName} on Port {Port}, Message: {Message}",
                    input.ModelName, input.Port, result.Message);
                
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "開啟模型時發生異常: {ModelName}", input.ModelName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 執行圖片推論。
    /// </summary>
    /// <param name="port">模型埠號</param>
    /// <param name="modelName">模型名稱（可選）</param>
    /// <param name="img">圖片檔案</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>推論結果</returns>
    [HttpPost("predict")]
    [ProducesResponseType(typeof(PredictResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Predict(
        [FromQuery] int port,
        [FromQuery] string? modelName,
        IFormFile img,
        CancellationToken cancellationToken)
    {
        if (img == null || img.Length == 0)
        {
            return BadRequest(new { error = "圖片檔案不能為空" });
        }

        try
        {
            // 設定推論 Port 的模型埠號（如果是 AinaviAiInferencePort）
            if (_inferencePort is AinaviAiInferencePort ainaviPort)
            {
                ainaviPort.SetModelPort(port);
            }

            // 讀取圖片資料
            await using var stream = img.OpenReadStream();
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            var imageBytes = memoryStream.ToArray();

            // 建立 ImageData（需要估算寬高，這裡簡化處理）
            // 實際應用中可能需要解析圖片元資料
            var imageData = new ImageData(
                imageBytes,
                Width: 0,  // 實際應用中應從圖片解析
                Height: 0, // 實際應用中應從圖片解析
                PixelFormat: "Bgr24");

            // 執行推論
            var prediction = await _inferencePort.PredictAsync(imageData, cancellationToken);

            // 建立推論記錄
            var logResult = new PredictResult(
                Timestamp: DateTime.Now,
                ModelName: modelName ?? _options.DefaultModel,
                Port: port,
                FileName: img.FileName,
                PredClass: prediction.Label,
                Confidence: prediction.Confidence,
                RawJson: JsonSerializer.Serialize(prediction));

            // 記錄到檔案
            await _logService.AppendLogAsync(logResult, cancellationToken);

            _logger.LogInformation(
                "推論完成: Model={ModelName}, Port={Port}, Class={Class}, Confidence={Confidence:P2}",
                modelName ?? _options.DefaultModel, port, prediction.Label, prediction.Confidence);

            return Ok(logResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推論時發生異常: Port={Port}, FileName={FileName}", port, img.FileName);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 取得推論記錄列表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>推論記錄列表</returns>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(IReadOnlyList<PredictResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLogs(CancellationToken cancellationToken)
    {
        try
        {
            var logs = await _logService.GetLogsAsync(cancellationToken);
            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得推論記錄時發生異常");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

/// <summary>
/// 開啟模型請求輸入。
/// </summary>
public sealed record OpenModelInput(string ModelName, int Port);

