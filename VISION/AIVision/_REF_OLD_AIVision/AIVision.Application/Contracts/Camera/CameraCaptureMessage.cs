using AIVision.Domain.Shared;

namespace AIVision.Application.Contracts.Camera;

public sealed record CameraCaptureMessage(ImageData Image);
