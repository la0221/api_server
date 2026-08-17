using AIVision.Application.MoldCode;
using AIVision.Application.Ports.MoldCode;
using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.Devices;
using Microsoft.Extensions.Options;
using InspectionEntity = AIVision.Domain.Entities.Inspection;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 離線端到端驗證 AreaRunService → VerifyMoldCodeCycleCommandHandler 接線:
/// FakePlc + FakeCamera + 樁辨識器,證明 讀碼→投票→三態→(模擬)氣吹 走得通。
/// (ONNX 真實準確率由 MoldCode.Harness 另行驗證,此處只驗編排接線。)
/// </summary>
public class AreaRunServiceTests
{
    private sealed class StubRecognizer : IMoldCodeRecognizerPort
    {
        private readonly MarkingObservation _obs;
        public StubRecognizer(MarkingObservation obs) => _obs = obs;
        public MarkingObservation Recognize(ImageData image, IReadOnlyList<string> classSet) => _obs;
    }

    /// <summary>捕捉 AddAsync 的假倉儲(只記下最後一筆,用於驗證持久化寫入)。</summary>
    private sealed class CapturingInspectionRepository : IInspectionRepository
    {
        public InspectionEntity? Saved { get; private set; }
        public int SaveCount { get; private set; }

        public Task AddAsync(InspectionEntity inspection, CancellationToken cancellationToken)
        {
            Saved = inspection;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<InspectionStatistics> GetStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
            => Task.FromResult(new InspectionStatistics(0, 0, 0));

        public Task<Dictionary<string, int>> GetDefectStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, int>());
    }

    private static MoldCodeCycleOptions BuildOptions() => new()
    {
        ClassSet = Array.Empty<string>(),
        MixedAlarmConfThreshold = 0.85,
        MaxFrames = 5,
        TimeBudgetMs = 5000,
        MinConsensusVotes = 3,
        MinConsensusMargin = 2
    };

    private static AreaRunService Build(MarkingObservation obs, out FakePlcPort plc)
        => Build(obs, out plc, repository: null);

    private static AreaRunService Build(MarkingObservation obs, out FakePlcPort plc, IInspectionRepository? repository)
    {
        plc = new FakePlcPort();
        var camera = new FakeCameraPort();
        var recognizer = new StubRecognizer(obs);
        var handler = new VerifyMoldCodeCycleCommandHandler(
            plc, camera, recognizer, Options.Create(BuildOptions()));
        return new AreaRunService(handler, handshake: null, logger: null, repository: repository);
    }

    [Fact]
    public async Task RunOnce_ReadMatchesExpected_ReportsOk_NoAirBlow()
    {
        var svc = Build(MarkingObservation.Read("M101/14", 1.0, 0.92), out var plc);
        MoldCodeCycleResult? raised = null;
        svc.CycleCompleted += (_, r) => raised = r;

        var result = await svc.RunOnceAsync("M101/14", CancellationToken.None);

        Assert.Equal(MarkingVerifyOutcome.Match, result.Outcome);
        Assert.False(result.AirBlown);
        Assert.True(plc.LastCommand.ResultOn);
        Assert.False(plc.LastCommand.AirBlow);
        Assert.Same(result, raised);   // CycleCompleted 有觸發且帶同一結果
    }

    [Fact]
    public async Task RunOnce_ConfidentMismatch_TriggersMixedAlarmAndAirBlow()
    {
        // 預期 M101/14,但高信心(0.95 ≥ 0.85)讀到 M101/03 → 混料 → 氣吹剔除
        var svc = Build(MarkingObservation.Read("M101/03", 1.0, 0.95), out var plc);

        var result = await svc.RunOnceAsync("M101/14", CancellationToken.None);

        Assert.Equal(MarkingVerifyOutcome.MixedAlarm, result.Outcome);
        Assert.True(result.AirBlown);
        Assert.True(plc.LastCommand.AirBlow);
    }

    [Fact]
    public async Task RunOnce_EmptyExpected_Throws()
    {
        var svc = Build(MarkingObservation.Read("M101/14", 1.0, 0.9), out _);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RunOnceAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task RunOnce_WithRepositoryAndWorkOrderId_PersistsInspectionWithOutcome()
    {
        // 高信心讀到不同碼 → MixedAlarm + 氣吹;設定 WorkOrderId 後應持久化一筆三態檢測記錄。
        var repo = new CapturingInspectionRepository();
        var svc = Build(MarkingObservation.Read("M101/03", 1.0, 0.95), out _, repo);
        var woid = Guid.NewGuid();
        svc.WorkOrderId = woid;

        var result = await svc.RunOnceAsync("M101/14", CancellationToken.None);

        Assert.Equal(1, repo.SaveCount);
        Assert.NotNull(repo.Saved);
        Assert.Equal(woid, repo.Saved!.WorkOrderId);
        Assert.Equal(MarkingVerifyOutcome.MixedAlarm.ToString(), repo.Saved.Outcome);
        Assert.Equal(result.Outcome.ToString(), repo.Saved.Result);
        Assert.True(repo.Saved.AirBlown);
        Assert.Equal("M101/14", repo.Saved.ExpectedCode);
        Assert.Equal(result.ReadCode, repo.Saved.ReadCode);
    }

    [Fact]
    public async Task RunOnce_WithRepositoryButNoWorkOrderId_DoesNotPersist()
    {
        var repo = new CapturingInspectionRepository();
        var svc = Build(MarkingObservation.Read("M101/14", 1.0, 0.92), out _, repo);
        // WorkOrderId 未設定 → 不持久化

        await svc.RunOnceAsync("M101/14", CancellationToken.None);

        Assert.Equal(0, repo.SaveCount);
        Assert.Null(repo.Saved);
    }
}
