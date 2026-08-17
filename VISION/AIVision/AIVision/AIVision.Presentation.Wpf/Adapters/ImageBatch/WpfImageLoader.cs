using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using AIVision.Presentation.Wpf.Utilities;

namespace AIVision.Presentation.Wpf.Adapters.ImageBatch;

public sealed class WpfImageLoader : IImageLoaderPort
{
    public Task<ImageData> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var image = BitmapSourceFactory.FromBitmapSource(bitmap);
        return Task.FromResult(image);
    }
}
