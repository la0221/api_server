namespace AIVision.Domain.User;

/// <summary>
/// 使用者角色（權限等級）
/// </summary>
public enum UserRole
{
    /// <summary>作業員 - 最低權限（僅限基本操作）</summary>
    Operator = 3,

    /// <summary>工程師 - 中等權限（設備調整、統計查看）</summary>
    Engineer = 2,

    /// <summary>廠商 - 最高權限（完整存取 + 權限配置）</summary>
    Vendor = 1
}
