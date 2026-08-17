using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace AIVision.Presentation.Wpf.Services.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void ShowWindow<TWindow>() where TWindow : Window
    {
        var window = CreateWindowInternal<TWindow>();
        window.Show();
    }

    public bool? ShowDialog<TWindow>() where TWindow : Window
    {
        var window = CreateWindowInternal<TWindow>();
        return window.ShowDialog();
    }

    public bool? ShowDialog<TWindow>(Action<TWindow> initializer) where TWindow : Window
    {
        var window = CreateWindowInternal<TWindow>();
        initializer?.Invoke(window);
        return window.ShowDialog();
    }

    public TWindow? CreateWindow<TWindow>() where TWindow : Window
    {
        return CreateWindowInternal<TWindow>();
    }

    private TWindow CreateWindowInternal<TWindow>() where TWindow : Window
    {
        var window = _serviceProvider.GetRequiredService<TWindow>();

        // 不設定 Owner，避免關閉子視窗時主視窗縮小
        // 改用 CenterScreen 定位
        if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window;
    }
}

