using Dapper;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public record IngestionChunkPayload(
       string DocumentId,
       int PageNumber,
       int ChunkIndex,
       string ExtractedText,
       string ParentBlobUrl
   );

    public interface IStagingBufferRepository
    {
        Task UpsertChunkBatchAsync(List<IngestionChunkPayload> chunks, CancellationToken cancellationToken);
    }

    public class StagingBufferRepository : IStagingBufferRepository
    {
        private readonly string _connectionString;

        public StagingBufferRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task UpsertChunkBatchAsync(List<IngestionChunkPayload> chunks, CancellationToken cancellationToken)
        {
            if (chunks == null || chunks.Count == 0) return;

            const string sqlMergeQuery = @"
                MERGE dbo.StagingIngestionBuffer AS Target
                USING (SELECT @DocumentId AS DocumentId, @PageNumber AS PageNumber, @ChunkIndex AS ChunkIndex) AS Source
                ON (Target.DocumentId = Source.DocumentId 
                    AND Target.PageNumber = Source.PageNumber 
                    AND Target.ChunkIndex = Source.ChunkIndex)
                
                -- CDC Scenario A: Text modified! Reset state machine flags for downstream vector workers.
                WHEN MATCHED AND Target.RowHash <> @RowHash THEN
                    UPDATE SET 
                        ExtractedText = @ExtractedText,
                        RowHash = @RowHash,
                        ParentBlobUrl = @ParentBlobUrl,
                        Status = 0,             -- Reset to Pending
                        VectorSynced = 0,       -- Requires Pinecone Sync
                        RetryCount = 0,
                        LockedUntil = NULL,
                        ErrorMessage = NULL
                        
                -- CDC Scenario B: Completely new chunk! Insert record directly.
                WHEN NOT MATCHED THEN
                    INSERT (DocumentId, PageNumber, ChunkIndex, ExtractedText, RowHash, ParentBlobUrl, Status, VectorSynced, RetryCount, CreatedAt)
                    VALUES (@DocumentId, @PageNumber, @ChunkIndex, @ExtractedText, @RowHash, @ParentBlobUrl, 0, 0, 0, SYSDATETIMEOFFSET());";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Explicitly wrapping in an atomic transaction
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            try
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Generate the deterministic binary hash signature
                    byte[] computedHash = IngestionHashEngine.ComputeSha256(chunk.ExtractedText);

                    var parameters = new DynamicParameters();
                    parameters.Add("@DocumentId", chunk.DocumentId, DbType.AnsiString, size: 250);
                    parameters.Add("@PageNumber", chunk.PageNumber, DbType.Int32);
                    parameters.Add("@ChunkIndex", chunk.ChunkIndex, DbType.Int32);
                    parameters.Add("@ExtractedText", chunk.ExtractedText, DbType.String);
                    parameters.Add("@ParentBlobUrl", chunk.ParentBlobUrl, DbType.AnsiString, size: 1000);
                    parameters.Add("@RowHash", computedHash, DbType.Binary, size: 32);

                    await connection.ExecuteAsync(sqlMergeQuery, parameters, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }
}
