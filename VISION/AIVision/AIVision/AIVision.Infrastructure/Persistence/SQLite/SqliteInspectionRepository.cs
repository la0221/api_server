using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteInspectionRepository : IInspectionRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public SqliteInspectionRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<InspectionStatistics> GetStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        // 模號三態語意:GOOD = Outcome IN ('Match','TrustInput');REJECT = Outcome = 'MixedAlarm';排除 'Skip'。
        const string sql = @"
            SELECT
                SUM(CASE WHEN Outcome IN ('Match','TrustInput','MixedAlarm') THEN 1 ELSE 0 END) AS TotalCount,
                SUM(CASE WHEN Outcome IN ('Match','TrustInput') THEN 1 ELSE 0 END) AS OkCount,
                SUM(CASE WHEN Outcome = 'MixedAlarm' THEN 1 ELSE 0 END) AS NgCount
            FROM Inspections
            WHERE WorkOrderId = @WorkOrderId;
        ";

        using var connection = _connectionFactory.CreateConnection();

        if (connection is SqliteConnection sqliteConnection)
        {
            await sqliteConnection.OpenAsync(cancellationToken);
            var result = await sqliteConnection.QueryFirstOrDefaultAsync<(int TotalCount, int OkCount, int NgCount)>(
                sql, new { WorkOrderId = workOrderId.ToString() });
            return new InspectionStatistics(result.TotalCount, result.OkCount, result.NgCount);
        }
        else
        {
            connection.Open();
            var result = connection.QueryFirstOrDefault<(int TotalCount, int OkCount, int NgCount)>(
                sql, new { WorkOrderId = workOrderId.ToString() });
            return new InspectionStatistics(result.TotalCount, result.OkCount, result.NgCount);
        }
    }

    public async Task<Dictionary<string, int>> GetDefectStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        // 模號核對改為依三態 Outcome 分組(取代舊 Defects 表瑕疵類型分組)。
        const string sql = @"
            SELECT Outcome AS OutcomeKey, COUNT(*) AS Count
            FROM Inspections
            WHERE WorkOrderId = @WorkOrderId
              AND Outcome IS NOT NULL
              AND Outcome != ''
            GROUP BY Outcome;
        ";

        using var connection = _connectionFactory.CreateConnection();

        if (connection is SqliteConnection sqliteConnection)
        {
            await sqliteConnection.OpenAsync(cancellationToken);
            var results = await sqliteConnection.QueryAsync<(string OutcomeKey, int Count)>(
                sql, new { WorkOrderId = workOrderId.ToString() });
            return results.ToDictionary(r => r.OutcomeKey, r => r.Count, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            connection.Open();
            var results = connection.Query<(string OutcomeKey, int Count)>(
                sql, new { WorkOrderId = workOrderId.ToString() });
            return results.ToDictionary(r => r.OutcomeKey, r => r.Count, StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task AddAsync(Inspection inspection, CancellationToken cancellationToken)
    {
        const string sqlInspection = @"
            INSERT INTO Inspections (Id, WorkOrderId, InspectedAt, Result, Confidence, ImagePath, AnnotatedImagePath, ModelVersion, InferenceTimeMs, ExpectedCode, ReadCode, Outcome, AirBlown, CreatedAt)
            VALUES (@Id, @WorkOrderId, @InspectedAt, @Result, @Confidence, @ImagePath, @AnnotatedImagePath, @ModelVersion, @InferenceTimeMs, @ExpectedCode, @ReadCode, @Outcome, @AirBlown, @CreatedAt);
        ";

        const string sqlDefect = @"
            INSERT INTO Defects (InspectionId, DefectType, Confidence, BoundingBoxX, BoundingBoxY, BoundingBoxWidth, BoundingBoxHeight, Severity, CreatedAt)
            VALUES (@InspectionId, @DefectType, @Confidence, @BoundingBoxX, @BoundingBoxY, @BoundingBoxWidth, @BoundingBoxHeight, @Severity, @CreatedAt);
        ";

        var inspectionRow = InspectionRow.FromEntity(inspection);

        using var connection = _connectionFactory.CreateConnection();

        if (connection is SqliteConnection sqliteConnection)
        {
            await sqliteConnection.OpenAsync(cancellationToken);

            // 使用 Serializable 隔離級別避免高併發時的死鎖
            using var transaction = sqliteConnection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                // 插入檢測記錄
                await sqliteConnection.ExecuteAsync(sqlInspection, inspectionRow, transaction);

                // 插入瑕疵記錄（如果有）
                if (inspection.Defects != null && inspection.Defects.Any())
                {
                    var defectRows = inspection.Defects.Select(d => DefectRow.FromEntity(inspection.Id, d));
                    await sqliteConnection.ExecuteAsync(sqlDefect, defectRows, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        else
        {
            // 備用方案（不使用 async，但兼容性更好）
            connection.Open();
            // 使用 Serializable 隔離級別避免高併發時的死鎖
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            try
            {
                connection.Execute(sqlInspection, inspectionRow, transaction);

                if (inspection.Defects != null && inspection.Defects.Any())
                {
                    var defectRows = inspection.Defects.Select(d => DefectRow.FromEntity(inspection.Id, d));
                    connection.Execute(sqlDefect, defectRows, transaction);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    // 輔助類
    private sealed class InspectionRow
    {
        public string Id { get; set; } = string.Empty;
        public string WorkOrderId { get; set; } = string.Empty;
        public string InspectedAt { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public float? Confidence { get; set; }
        public string? ImagePath { get; set; }
        public string? AnnotatedImagePath { get; set; }
        public string ModelVersion { get; set; } = string.Empty;
        public int? InferenceTimeMs { get; set; }
        public string? ExpectedCode { get; set; }
        public string? ReadCode { get; set; }
        public string? Outcome { get; set; }
        public int? AirBlown { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public static InspectionRow FromEntity(Inspection entity)
        {
            return new InspectionRow
            {
                Id = entity.Id.ToString(),
                WorkOrderId = entity.WorkOrderId.ToString(),
                InspectedAt = entity.At.ToString("O"),
                Result = entity.Result,
                Confidence = entity.Confidence,
                ImagePath = entity.ImagePath,
                AnnotatedImagePath = entity.AnnotatedImagePath,
                ModelVersion = entity.ModelVersion,
                InferenceTimeMs = entity.InferenceTimeMs,
                ExpectedCode = entity.ExpectedCode,
                ReadCode = entity.ReadCode,
                Outcome = entity.Outcome,
                AirBlown = entity.AirBlown.HasValue ? (entity.AirBlown.Value ? 1 : 0) : (int?)null,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
        }
    }

    private sealed class DefectRow
    {
        public string InspectionId { get; set; } = string.Empty;
        public string DefectType { get; set; } = string.Empty;
        public float? Confidence { get; set; }
        public int? BoundingBoxX { get; set; }
        public int? BoundingBoxY { get; set; }
        public int? BoundingBoxWidth { get; set; }
        public int? BoundingBoxHeight { get; set; }
        public string? Severity { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        public static DefectRow FromEntity(Guid inspectionId, Defect defect)
        {
            return new DefectRow
            {
                InspectionId = inspectionId.ToString(),
                DefectType = defect.Type,
                Confidence = defect.Confidence,
                BoundingBoxX = defect.BoundingBox?.X,
                BoundingBoxY = defect.BoundingBox?.Y,
                BoundingBoxWidth = defect.BoundingBox?.Width,
                BoundingBoxHeight = defect.BoundingBox?.Height,
                Severity = defect.Severity,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
