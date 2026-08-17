using AIVision.Application.Contracts;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Shared;
using MediatR;
using InspectionEntity = AIVision.Domain.Entities.Inspection;

namespace AIVision.Application.Inspection.Commands;

public sealed class StartInspectionCycleCommandHandler
    : IRequestHandler<StartInspectionCycleCommand, InspectionResultDto>
{
    private readonly IPlcPort _plc;
    private readonly ICameraPort _camera;
    private readonly IAiInferencePort _ai;
    private readonly IInspectionRepository _inspectionRepository;

    public StartInspectionCycleCommandHandler(
        IPlcPort plc,
        ICameraPort camera,
        IAiInferencePort ai,
        IInspectionRepository inspectionRepository)
    {
        _plc = plc;
        _camera = camera;
        _ai = ai;
        _inspectionRepository = inspectionRepository;
    }

    public async Task<InspectionResultDto> Handle(
        StartInspectionCycleCommand request,
        CancellationToken cancellationToken)
    {
        await _plc.WriteAsync(IoCommand.CaptureStartCommand(), cancellationToken);

        var image = await _camera.CaptureOnceAsync(cancellationToken);
        var prediction = await _ai.PredictAsync(image, cancellationToken);

        await _plc.WriteAsync(IoCommand.Result(prediction.IsOk), cancellationToken);

        var inspection = new InspectionEntity(
            request.WorkOrderId,
            prediction.Label,
            prediction.Confidence,
            prediction.ImagePath,
            prediction.ModelVersion);

        await _inspectionRepository.AddAsync(inspection, cancellationToken);

        return new InspectionResultDto(
            inspection.Id,
            inspection.Result,
            inspection.Confidence,
            InferenceTimeMs: null,
            Defects: null,
            OriginalImage: null,
            AnnotatedImage: null);
    }
}
