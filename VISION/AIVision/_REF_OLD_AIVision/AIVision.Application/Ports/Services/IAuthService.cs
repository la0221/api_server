using AIVision.Domain.User;

namespace AIVision.Application.Ports.Services;

/// <summary>
/// 認證服務介面
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 登入驗證
    /// </summary>
    /// <param name="username">帳號</param>
    /// <param name="password">密碼</param>
    /// <returns>是否登入成功</returns>
    bool Login(string username, string password);

    /// <summary>
    /// 登出
    /// </summary>
    void Logout();

    /// <summary>
    /// 當前使用者帳號（未登入時為 null）
    /// </summary>
    string? CurrentUsername { get; }

    /// <summary>
    /// 當前使用者角色
    /// </summary>
    UserRole CurrentRole { get; }

    /// <summary>
    /// 當前使用者顯示名稱
    /// </summary>
    string CurrentDisplayName { get; }

    /// <summary>
    /// 檢查當前使用者是否有指定權限
    /// </summary>
    /// <param name="requiredRole">需要的最低權限角色</param>
    /// <returns>是否有權限</returns>
    bool HasPermission(UserRole requiredRole);

    /// <summary>
    /// 登入狀態變更事件
    /// </summary>
    event EventHandler? LoginStateChanged;
}
