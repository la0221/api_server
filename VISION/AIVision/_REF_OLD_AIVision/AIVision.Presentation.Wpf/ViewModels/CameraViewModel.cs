using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVision.Application.Contracts.Camera;
using AIVision.Application.Ports.Devices;
using AIVision.Application.ViewModels.Camera;
using AIVision.Domain.Shared;
using AIVision.Presentation.Wpf.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class CameraViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICameraDiscoveryPort _discovery;
    private readonly ICameraPort _camera;
    private readonly ICameraControlPort _control;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _parameterSync = new(1, 1);

    public CameraViewModel(
        ICameraDiscoveryPort discovery,
        ICameraPort camera,
        ICameraControlPort control,
        IMessenger messenger)
    {
        _discovery = discovery;
        _camera = camera;
        _control = control;
        _messenger = messenger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _camera.FrameReceived += OnFrameReceived;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedCamera) or nameof(IsPreviewing) or nameof(IsBusy))
            {
                StartPreviewCommand.NotifyCanExecuteChanged();
                StopPreviewCommand.NotifyCanExecuteChanged();
                CaptureOnceCommand.NotifyCanExecuteChanged();
            }
        };

        CameraParameters.CollectionChanged += OnCameraParametersChanged;
    }

    public ObservableCollection<CameraDeviceVm> AvailableCameras { get; } = new();

    public ObservableCollection<CameraParameterViewModel> CameraParameters { get; } = new();

    [ObservableProperty]
    private CameraDeviceVm? selectedCamera;

    [ObservableProperty]
    private BitmapSource? frameBitmap;

    [ObservableProperty]
    private bool isPreviewing;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "準備就緒";

    public bool HasCameraParameters => CameraParameters.Count > 0;

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var devices = await _discovery.ListAsync(CancellationToken.None);

            AvailableCameras.Clear();
            foreach (var device in devices)
            {
                AvailableCameras.Add(device);
            }

            if (AvailableCameras.Count > 0 && SelectedCamera is null)
            {
                SelectedCamera = AvailableCameras[0];
            }
            else if (SelectedCamera is not null && AvailableCameras.All(d => d.Id != SelectedCamera.Id))
            {
                SelectedCamera = AvailableCameras.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadParametersAsync()
    {
        try
        {
            var descriptors = await _control.GetParametersAsync(CancellationToken.None).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                CameraParameters.Clear();
                foreach (var descriptor in descriptors)
                {
                    CameraParameters.Add(new CameraParameterViewModel(this, descriptor));
                }
                if (CameraParameters.Count > 0)
                {
                    StatusMessage = "已載入相機參數設定。";
                }
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                CameraParameters.Clear();
                StatusMessage = $"載入相機參數失敗：{ex.Message}";
            });
        }
    }

    private bool CanStartPreview() => !IsBusy && !IsPreviewing && SelectedCamera is not null;
    private bool CanStopPreview() => IsPreviewing && !IsBusy;
    private bool CanCaptureOnce() => SelectedCamera is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartPreview))]
    private async Task StartPreviewAsync()
    {
        if (SelectedCamera is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _camera.OpenAsync(SelectedCamera.Id, CancellationToken.None);
            await _camera.StartPreviewAsync(CancellationToken.None);
            IsPreviewing = true;
            StatusMessage = $"預覽中 - {SelectedCamera.Display}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"預覽啟動失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopPreview))]
    private async Task StopPreviewAsync()
    {
        try
        {
            IsBusy = true;
            await _camera.StopPreviewAsync(CancellationToken.None);
            IsPreviewing = false;
            StatusMessage = "預覽已停止";
        }
        catch (Exception ex)
        {
            StatusMessage = $"停止預覽失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCaptureOnce))]
    private async Task CaptureOnceAsync()
    {
        if (SelectedCamera is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            if (!_camera.IsOpen)
            {
            await _camera.OpenAsync(SelectedCamera.Id, CancellationToken.None);
        }

        var image = await _camera.CaptureOnceAsync(CancellationToken.None);
            UpdateBitmap(image);
            _messenger.Send(new CameraCaptureMessage(image));
            StatusMessage = "已完成單次取像";
        }
        catch (Exception ex)
        {
            StatusMessage = $"取像失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ApplyParameterAsync(CameraParameterViewModel parameter, double value)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            await _parameterSync.WaitAsync().ConfigureAwait(false);
            IsBusy = true;
            StatusMessage = $"正在套用 {parameter.DisplayName}，等待首幀…";
            await _control.SetParameterAsync(parameter.Kind, value, CancellationToken.None).ConfigureAwait(false);
            StatusMessage = $"{parameter.DisplayName} 已更新為 {parameter.ValueString}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"更新 {parameter.DisplayName} 失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _parameterSync.Release();
        }
    }

    private void OnFrameReceived(object? sender, ImageData image)
    {
        try
        {
            UpdateBitmap(image);
        }
        catch (Exception ex)
        {
            StatusMessage = $"顯示影像失敗：{ex.Message}";
        }
    }

    private void UpdateBitmap(ImageData image)
    {
        _dispatcher.BeginInvoke(() =>
        {
            FrameBitmap = BitmapSourceFactory.FromImageData(image);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _camera.FrameReceived -= OnFrameReceived;

        try
        {
            await _camera.StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        await _camera.DisposeAsync().ConfigureAwait(false);
        _parameterSync.Dispose();
    }

    private void OnCameraParametersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCameraParameters));
    }
}
