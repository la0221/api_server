using AIVision.Application.Inspection.Commands;
using AIVision.Infrastructure.Devices;
using AIVision.Infrastructure.Persistence;

namespace AIVision.Application.Tests.Inspection;

public class StartInspectionCycleCommandHandlerTests
{
    [Fact]
    public async Task Should_Write_Result_To_Plc_When_Cycle_Completes()
    {
        var plc = new FakePlcPort();
        var camera = new FakeCameraPort();
        var ai = new FakeAiInferencePort(isOk: true);
        var inspectionRepository = new InMemoryInspectionRepository();

        var handler = new StartInspectionCycleCommandHandler(plc, camera, ai, inspectionRepository);

        var dto = await handler.Handle(new StartInspectionCycleCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("OK", dto.Result);
        Assert.True(plc.LastCommand.ResultOn);
    }
}
