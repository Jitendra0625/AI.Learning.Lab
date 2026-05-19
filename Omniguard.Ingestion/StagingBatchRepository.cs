using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Omniguard.Ingestion
{
    public interface IStagingBatchRepository
    {
        Task<IReadOnlyList<StagingBufferItem>> AllocateProcessingBatchAsync(int batchSize, int lockDurationMinutes);
        Task CompleteBatchAsync(IEnumerable<long> successSequenceIds, IEnumerable<(long SequenceId, string Error)> failures);
    }

    public class StagingBatchRepository : IStagingBatchRepository
    {
        private readonly string _connectionString;

        public StagingBatchRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public async Task<IReadOnlyList<StagingBufferItem>> AllocateProcessingBatchAsync(int batchSize, int lockDurationMinutes)
        {
            const string sql = @"
    DECLARE @AllocatedRows TABLE (
        [SequenceId] BIGINT, 
        [DocumentId] VARCHAR(250), 
        [PageNumber] INT, 
        [ChunkIndex] INT, 
        [ExtractedText] NVARCHAR(MAX), 
        [VectorId] VARCHAR(300), 
        [ParentBlobUrl] VARCHAR(1000)
    );

    WITH TargetRows AS (
        SELECT TOP (@BatchSize) 
            [SequenceId], 
            [Status], 
            [LockedUntil], 
            [RetryCount],
            [DocumentId],   
            [PageNumber],   
            [ChunkIndex],   
            [ExtractedText],
            [VectorId],     
            [ParentBlobUrl] 
        FROM [dbo].[StagingIngestionBuffer] WITH (UPDLOCK, READPAST)
        WHERE ([Status] = 0 OR [Status] = 3) 
          AND ([LockedUntil] IS NULL OR [LockedUntil] < SYSDATETIMEOFFSET()) 
          AND [RetryCount] < 3
        ORDER BY [SequenceId] ASC
    )
    UPDATE TargetRows
    SET [Status] = 1, 
        [LockedUntil] = DATEADD(minute, @LockDurationMinutes, SYSDATETIMEOFFSET()), 
        [RetryCount] = [RetryCount] + 1
    OUTPUT 
        INSERTED.[SequenceId], 
        INSERTED.[DocumentId], 
        INSERTED.[PageNumber], 
        INSERTED.[ChunkIndex], 
        INSERTED.[ExtractedText], 
        INSERTED.[VectorId], 
        INSERTED.[ParentBlobUrl]
    INTO @AllocatedRows;

    SELECT * FROM @AllocatedRows;";

            using var connection = new SqlConnection(_connectionString);
            var items = await connection.QueryAsync<StagingBufferItem>(sql, new { BatchSize = batchSize, LockDurationMinutes = lockDurationMinutes });
            return items.ToList().AsReadOnly();
        }

        public async Task CompleteBatchAsync(IEnumerable<long> successSequenceIds, IEnumerable<(long SequenceId, string Error)> failures)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            try
            {
                if (successSequenceIds.Any())
                {
                    const string successSql = @"
                        UPDATE [dbo].[StagingIngestionBuffer]
                        SET [Status] = 2, [VectorSynced] = 1, [LockedUntil] = NULL, [ErrorMessage] = NULL
                        WHERE [SequenceId] IN @Ids;";
                    await connection.ExecuteAsync(successSql, new { Ids = successSequenceIds }, transaction);
                }

                if (failures.Any())
                {
                    const string failureSql = @"
                        UPDATE [dbo].[StagingIngestionBuffer]
                        SET [Status] = 3, [LockedUntil] = NULL, [ErrorMessage] = @Error
                        WHERE [SequenceId] = @SequenceId;";

                    foreach (var failure in failures)
                    {
                        await connection.ExecuteAsync(failureSql, new { SequenceId = failure.SequenceId, Error = failure.Error }, transaction);
                    }
                }
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }

    public class StagingBufferItem
    {
        public long SequenceId { get; set; }
        public string DocumentId { get; set; } = null!;
        public int PageNumber { get; set; }
        public int ChunkIndex { get; set; }
        public string ExtractedText { get; set; } = null!;
        public string VectorId { get; set; } = null!;
        public string ParentBlobUrl { get; set; } = null!;
    }
}