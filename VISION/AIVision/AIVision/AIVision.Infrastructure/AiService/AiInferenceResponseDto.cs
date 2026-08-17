using System.Collections.Generic;

namespace AIVision.Infrastructure.AiService;

internal sealed class AiInferenceResponseDto
{
    public string Label { get; set; } = string.Empty;

    public float Confidence { get; set; }

    public bool IsOk { get; set; }

    public string ModelVersion { get; set; } = string.Empty;

    public string? ImagePath { get; set; }

    public List<AiDetectionDto>? Detections { get; set; }
}

internal sealed class AiDetectionDto
{
    public string Label { get; set; } = string.Empty;

    public float Confidence { get; set; }

    public BoundingBoxDto? Box { get; set; }
}

internal sealed class BoundingBoxDto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
