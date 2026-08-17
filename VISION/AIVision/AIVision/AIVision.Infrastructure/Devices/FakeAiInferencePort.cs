using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;

namespace AIVision.Infrastructure.Devices;

public sealed class FakeAiInferencePort : IAiInferencePort
{
    private readonly bool _isOk;

    public FakeAiInferencePort(bool isOk = true)
    {
        _isOk = isOk;
    }

    /// <summary>
    /// Fake 實作永遠回報已連線
    /// </summary>
    public bool IsConnected => true;

    public Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken)
    {
        var label = _isOk ? "OK" : "NG";
        var prediction = new Prediction(label, 0.99f, _isOk, "FAKE-1.0", null);
        return Task.FromResult(prediction);
    }

    /// <summary>
    /// Fake 實作永遠回報健康
    /// </summary>
    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
