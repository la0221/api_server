using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AIVision.Application.Ports.ImageBatch;

namespace AIVision.Presentation.Wpf.Adapters.ImageBatch;

public sealed class FolderPickerPort : IFolderPickerPort
{
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        using var dialog = new FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = "選擇要複判的影像根資料夾"
        };

        var result = dialog.ShowDialog();
        return Task.FromResult(result == DialogResult.OK ? dialog.SelectedPath : null);
    }
}
