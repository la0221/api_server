using AIVision.Application.Contracts;
using MediatR;

namespace AIVision.Application.Inspection.Commands;

public sealed record StartInspectionCycleCommand(Guid WorkOrderId) : IRequest<InspectionResultDto>;
