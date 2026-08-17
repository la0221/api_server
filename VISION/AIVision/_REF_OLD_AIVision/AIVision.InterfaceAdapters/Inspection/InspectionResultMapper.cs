using AIVision.Application.Contracts;

namespace AIVision.InterfaceAdapters.Inspection;

public static class InspectionResultMapper
{
    public static InspectionResultResponse ToResponse(this InspectionResultDto dto) =>
        new(dto.Id, dto.Result, dto.Confidence);
}

public sealed record InspectionResultResponse(Guid Id, string Result, float? Confidence);
