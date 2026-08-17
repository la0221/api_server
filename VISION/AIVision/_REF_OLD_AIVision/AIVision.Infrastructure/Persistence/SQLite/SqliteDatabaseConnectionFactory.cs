using Microsoft.Data.Sqlite;
using System.Data;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteDatabaseConnectionFactory : IDatabaseConnectionFactory
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SqliteDatabaseConnectionFactory(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // 連線池優化設定
            Pooling = true,           // 啟用連線池
            DefaultTimeout = 30       // 連線超時 30 秒
        };
        _connectionString = builder.ToString();
    }

    public IDbConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            using var connection = CreateConnection();

            if (connection is SqliteConnection sqliteConnection)
            {
                await sqliteConnection.OpenAsync(cancellationToken);
                await ExecuteSchemaAsync(sqliteConnection, cancellationToken);
            }
            else
            {
                connection.Open();
                await ExecuteSchemaAsync(connection, cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task ExecuteSchemaAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        var sql = @"
            -- 啟用外鍵約束
            PRAGMA foreign_keys = ON;

            -- 工單表
            CREATE TABLE IF NOT EXISTS WorkOrders (
                Id TEXT PRIMARY KEY,
                Code TEXT NOT NULL UNIQUE,
                ProductName TEXT NOT NULL,
                ModelName TEXT,
                MachineModelName TEXT,
                StartAt TEXT NOT NULL,
                EndAt TEXT,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            -- 檢測記錄表
            CREATE TABLE IF NOT EXISTS Inspections (
                Id TEXT PRIMARY KEY,
                WorkOrderId TEXT NOT NULL,
                InspectedAt TEXT NOT NULL,
                Result TEXT NOT NULL,
                Confidence REAL,
                ImagePath TEXT,
                AnnotatedImagePath TEXT,
                ModelVersion TEXT NOT NULL,
                InferenceTimeMs INTEGER,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (WorkOrderId) REFERENCES WorkOrders(Id) ON DELETE CASCADE
            );

            -- 瑕疵記錄表
            CREATE TABLE IF NOT EXISTS Defects (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                InspectionId TEXT NOT NULL,
                DefectType TEXT NOT NULL,
                Confidence REAL,
                BoundingBoxX INTEGER,
                BoundingBoxY INTEGER,
                BoundingBoxWidth INTEGER,
                BoundingBoxHeight INTEGER,
                Severity TEXT,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (InspectionId) REFERENCES Inspections(Id) ON DELETE CASCADE
            );

            -- 索引
            CREATE INDEX IF NOT EXISTS idx_inspections_workorder ON Inspections(WorkOrderId);
            CREATE INDEX IF NOT EXISTS idx_inspections_result ON Inspections(Result);
            CREATE INDEX IF NOT EXISTS idx_inspections_inspectedat ON Inspections(InspectedAt);
            CREATE INDEX IF NOT EXISTS idx_defects_inspection ON Defects(InspectionId);
            CREATE INDEX IF NOT EXISTS idx_defects_type ON Defects(DefectType);
            CREATE INDEX IF NOT EXISTS idx_workorders_status ON WorkOrders(Status);
            CREATE INDEX IF NOT EXISTS idx_workorders_startat ON WorkOrders(StartAt);
        ";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (command is SqliteCommand sqliteCommand)
        {
            await sqliteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await Task.Run(() => command.ExecuteNonQuery(), cancellationToken);
        }

        // 數據庫遷移：添加 MachineModelName 欄位（如果不存在）
        var migrationSql = @"
            -- 檢查並添加 MachineModelName 欄位
            SELECT COUNT(*) as cnt FROM pragma_table_info('WorkOrders') WHERE name='MachineModelName';
        ";

        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = migrationSql;

        object? result;
        if (checkCommand is SqliteCommand sqliteCheckCommand)
        {
            result = await sqliteCheckCommand.ExecuteScalarAsync(cancellationToken);
        }
        else
        {
            result = await Task.Run(() => checkCommand.ExecuteScalar(), cancellationToken);
        }

        var columnExists = Convert.ToInt32(result) > 0;

        if (!columnExists)
        {
            var alterTableSql = "ALTER TABLE WorkOrders ADD COLUMN MachineModelName TEXT;";
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = alterTableSql;

            if (alterCommand is SqliteCommand sqliteAlterCommand)
            {
                await sqliteAlterCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await Task.Run(() => alterCommand.ExecuteNonQuery(), cancellationToken);
            }
        }
    }

}
