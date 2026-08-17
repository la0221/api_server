using System;
using System.Windows;
using AIVision.Presentation.Wpf.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace AIVision.Presentation.Wpf.Views;

public partial class CameraView : Window
{
    private readonly CameraViewModel _viewModel;

    public CameraView(CameraViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.RefreshDevicesCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }

        if (_viewModel.LoadParametersCommand is IAsyncRelayCommand loadCommand)
        {
            await loadCommand.ExecuteAsync(null);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.DisposeAsync();
    }
}
