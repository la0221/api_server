using System.Data;

namespace AIVision.Infrastructure.Persistence.SQLite;

/// <summary>
/// 數據庫連接工廠接口
/// </summary>
public interface IDatabaseConnectionFactory
{
    /// <summary>創建數據庫連接</summary>
    IDbConnection CreateConnection();

    /// <summary>初始化數據庫（創建表、索引等）</summary>
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
}
