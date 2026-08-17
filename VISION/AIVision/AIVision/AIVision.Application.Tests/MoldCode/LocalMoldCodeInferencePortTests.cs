using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.MoldCode.Onnx;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 驗證 <see cref="LocalMoldCodeInferencePort"/> 將本地辨識器的 <see cref="MarkingObservation"/>
/// 正確映射為 <see cref="Prediction"/>(取代舊 HTTP 推論;不會 hang)。
/// </summary>
public class LocalMoldCodeInferencePortTests
{
    /// <summary>回傳固定觀測結果的樁辨識器,並記錄收到的 classSet。</summary>
    private sealed class FakeRecognizer : IMoldCodeRecognizerPort
    {
        private readonly MarkingObservation _obs;
        public IReadOnlyList<string>? ReceivedClassSet { get; private set; }

        public FakeRecognizer(MarkingObservation obs) => _obs = obs;

        public MarkingObservation Recognize(ImageData image, IReadOnlyList<string> classSet)
        {
            ReceivedClassSet = classSet;
            return _obs;
        }
    }

    /// <summary>提供固定 CurrentClassSet / CurrentModelPath 的樁模型切換器(LoadModel 無作用)。</summary>
    private sealed class FakeModelSwitch : IMoldCodeModelSwitch
    {
        public string? CurrentModelPath { get; init; }
        public IReadOnlyList<string> CurrentClassSet { get; init; } = Array.Empty<string>();
        public void LoadModel(string modelPath, IReadOnlyList<string> classNames, string codePrefix) { }
    }

    private static ImageData DummyImage() =>
        new(new byte[] { 0, 0, 0 }, 1, 1, "Bgr24");

    [Fact]
    public async Task PredictAsync_WhenHasCode_MapsCodeAndConfidenceAndIsOk()
    {
        var obs = MarkingObservation.Read("M101/04", confDetect: 1.0, confClassify: 0.87);
        var recognizer = new FakeRecognizer(obs);
        var modelSwitch = new FakeModelSwitch
        {
            CurrentModelPath = @"D:\AIVisionModels\moldcode_v3_18cls.onnx",
            CurrentClassSet = new[] { "M101/04", "M101/05" }
        };
        var port = new LocalMoldCodeInferencePort(recognizer, modelSwitch);

        var prediction = await port.PredictAsync(DummyImage(), CancellationToken.None);

        Assert.Equal("M101/04", prediction.Label);
        Assert.True(prediction.IsOk);
        Assert.Equal(0.87f, prediction.Confidence, 5);
        Assert.Equal(modelSwitch.CurrentModelPath, prediction.ModelVersion);
        // 確認轉接器把目前字集傳給辨識器(用選定的本地模型字集)。
        Assert.Equal(modelSwitch.CurrentClassSet, recognizer.ReceivedClassSet);
    }

    [Fact]
    public async Task PredictAsync_WhenFailed_MapsToNoneAndNotOk()
    {
        var obs = MarkingObservation.Failed("no class within closed set");
        var port = new LocalMoldCodeInferencePort(new FakeRecognizer(obs), new FakeModelSwitch());

        var prediction = await port.PredictAsync(DummyImage(), CancellationToken.None);

        Assert.Equal("(none)", prediction.Label);
        Assert.False(prediction.IsOk);
        Assert.Equal(0f, prediction.Confidence);
        // CurrentModelPath 為 null 時退回常數 "moldcode"。
        Assert.Equal("moldcode", prediction.ModelVersion);
    }

    [Fact]
    public async Task IsConnected_And_HealthCheck_AreAlwaysTrue()
    {
        var port = new LocalMoldCodeInferencePort(
            new FakeRecognizer(MarkingObservation.Failed("x")),
            new FakeModelSwitch());

        Assert.True(port.IsConnected);
        Assert.True(await port.HealthCheckAsync(CancellationToken.None));
    }
}
