using System.Collections.Generic;

namespace AIVision.Infrastructure.AiService;

internal sealed class AiInferenceRequestDto
{
    public string ImageBase64 { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public string PixelFormat { get; set; } = string.Empty;

    public string? Model { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}
