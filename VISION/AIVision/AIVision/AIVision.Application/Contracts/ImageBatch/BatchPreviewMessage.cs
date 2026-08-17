using AIVision.Domain.Shared;

namespace AIVision.Application.Contracts.ImageBatch;

public sealed record BatchPreviewMessage(ImageData Image, string SourcePath);
