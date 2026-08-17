using System;
using AIVision.Application.Ports.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _authService;

    public event EventHandler<bool>? RequestClose;

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
    }

    [ObservableProperty]
    private string _username = string.Empty;

    public string Password { get; set; } = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand]
    private void Login()
    {
        // 清除錯誤訊息
        ErrorMessage = string.Empty;
        HasError = false;

        // 驗證輸入
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "請輸入帳號";
            HasError = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "請輸入密碼";
            HasError = true;
            return;
        }

        // 執行登入
        var success = _authService.Login(Username, Password);

        if (success)
        {
            RequestClose?.Invoke(this, true);
        }
        else
        {
            ErrorMessage = "帳號或密碼錯誤";
            HasError = true;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }
}
