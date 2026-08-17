using AIVision.Application.Inspection.Commands;
using AIVision.InterfaceAdapters.Inspection;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace AIVision.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class InspectionController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ILogger<InspectionController> _logger;

    public InspectionController(ISender sender, ILogger<InspectionController> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    [HttpPost("cycle")]
    [ProducesResponseType(typeof(InspectionResultResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunCycle(
        [FromBody] StartInspectionCycleRequest request,
        CancellationToken cancellationToken)
    {
        var command = new StartInspectionCycleCommand(request.WorkOrderId ?? Guid.NewGuid());
        var result = await _sender.Send(command, cancellationToken);
        var response = result.ToResponse();

        _logger.LogInformation("Inspection cycle completed {@Response}", response);

        return Ok(response);
    }
}

public sealed record StartInspectionCycleRequest(Guid? WorkOrderId);
