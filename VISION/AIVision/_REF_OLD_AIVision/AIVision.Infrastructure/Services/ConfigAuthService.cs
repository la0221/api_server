using AIVision.Application.Ports.Services;
using AIVision.Domain.User;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 基於 appsettings.json 配置的認證服務
/// </summary>
public sealed class ConfigAuthService : IAuthService
{
    private readonly ILogger<ConfigAuthService>? _logger;
    private readonly List<UserConfig> _users;
    private readonly UserRole _defaultRole;

    private string? _currentUsername;
    private UserRole _currentRole;
    private string _currentDisplayName;

    public event EventHandler? LoginStateChanged;

    public ConfigAuthService(IConfiguration configuration, ILogger<ConfigAuthService>? logger = null)
    {
        _logger = logger;

        // 讀取配置
        var authSection = configuration.GetSection("Authentication");

        // 預設角色
        var defaultRoleStr = authSection["DefaultRole"] ?? "Operator";
        _defaultRole = Enum.TryParse<UserRole>(defaultRoleStr, true, out var role) ? role : UserRole.Operator;

        // 讀取使用者列表
        _users = authSection.GetSection("Users").Get<List<UserConfig>>() ?? new List<UserConfig>();

        // 初始化為預設角色
        _currentRole = _defaultRole;
        _currentDisplayName = GetRoleDisplayName(_defaultRole);

        _logger?.LogInformation("[Auth] 認證服務初始化完成 - 預設角色: {Role}, 已配置使用者: {Count}",
            _defaultRole, _users.Count);
    }

    public string? CurrentUsername => _currentUsername;

    public UserRole CurrentRole => _currentRole;

    public string CurrentDisplayName => _currentDisplayName;

    public bool Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger?.LogWarning("[Auth] 登入失敗 - 帳號或密碼為空");
            return false;
        }

        // 尋找匹配的使用者
        var user = _users.FirstOrDefault(u =>
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
            u.Password == password);

        if (user == null)
        {
            _logger?.LogWarning("[Auth] 登入失敗 - 帳號或密碼錯誤: {Username}", username);
            return false;
        }

        // 解析角色
        if (!Enum.TryParse<UserRole>(user.Role, true, out var role))
        {
            _logger?.LogWarning("[Auth] 登入失敗 - 無效的角色設定: {Role}", user.Role);
            return false;
        }

        // 登入成功
        _currentUsername = username;
        _currentRole = role;
        _currentDisplayName = !string.IsNullOrEmpty(user.DisplayName)
            ? $"{user.DisplayName} ({GetRoleDisplayName(role)})"
            : $"{username} ({GetRoleDisplayName(role)})";

        _logger?.LogInformation("[Auth] 登入成功 - 使用者: {Username}, 角色: {Role}", username, role);

        LoginStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Logout()
    {
        var previousUser = _currentUsername;
        _currentUsername = null;
        _currentRole = _defaultRole;
        _currentDisplayName = GetRoleDisplayName(_defaultRole);

        _logger?.LogInformation("[Auth] 已登出 - 之前使用者: {Username}, 重置為: {Role}",
            previousUser ?? "(無)", _defaultRole);

        LoginStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasPermission(UserRole requiredRole)
    {
        // UserRole 數值越小權限越高
        return (int)_currentRole <= (int)requiredRole;
    }

    private static string GetRoleDisplayName(UserRole role) => role switch
    {
        UserRole.Operator => "作業員",
        UserRole.Engineer => "工程師",
        UserRole.Vendor => "廠商",
        _ => role.ToString()
    };

    /// <summary>
    /// 使用者配置（對應 appsettings.json）
    /// </summary>
    private sealed class UserConfig
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "Operator";
        public string? DisplayName { get; set; }
    }
}
