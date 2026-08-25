using System;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Devices.Blow;

/// <summary>
/// 吹氣觸發輸出（只寫 log）——開發機沒有 IO 監聽程式、或驗收時只想確認
/// 「哪幾片會被吹、延遲多久」時用。行為與 TCP 版完全一致，只差沒真的送出去。
/// </summary>
public sealed class LogBlowOutput : IBlowOutputPort
{
    private readonly ILogger<LogBlowOutput>? _logger;

    public LogBlowOutput(ILogger<LogBlowOutput>? logger = null) => _logger = logger;

    public string DisplayName => "只寫 log（不實際送出）";

    public Task SendAsync(BlowRequest request, CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "[Blow→Log] id={Id} reason={Reason} expect={Expect} got={Got} conf={ConfM:0.00}/{ConfX:0.00}",
            request.Id, request.Reason,
            $"{request.ExpectedMohao}/{request.ExpectedXuehao}",
            $"{request.DetectedMohao}/{request.DetectedXuehao}",
            request.ConfMohao, request.ConfXuehao);
        return Task.CompletedTask;
    }
}
