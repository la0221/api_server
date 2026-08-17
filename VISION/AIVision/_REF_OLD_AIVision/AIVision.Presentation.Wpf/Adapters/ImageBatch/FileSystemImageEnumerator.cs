using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;

namespace AIVision.Presentation.Wpf.Adapters.ImageBatch;

public sealed class FileSystemImageEnumerator : IImageEnumeratorPort
{
    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public async IAsyncEnumerable<string> EnumerateAsync(string rootPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(file);
            if (ext is null || !SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
            await Task.Yield();
        }
    }
}
