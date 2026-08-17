namespace AIVision.Application.ViewModels.Camera;

public sealed class CameraDeviceVm
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";

    public override string ToString() => Display;
}
