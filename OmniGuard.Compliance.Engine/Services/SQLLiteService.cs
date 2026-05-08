using Dapper;
using Microsoft.Data.Sqlite;
using OmniGuard.Compliance.Engine.Models;

namespace OmniGuard.Compliance.Engine.Services
{
    public class SQLLiteService
    {
        // Run this once before starting your ingestion
        public async Task PrepareLexicalLayer()
        {
            using var connection = new SqliteConnection("Data Source=OmniGuard.db");
            await connection.OpenAsync();

            //The 'tokenize' porter helps with word variations(e.g., "mortgage" vs "mortgages")
            // Using FTS5 for BM25 keyword ranking
            await connection.ExecuteAsync(@"
        CREATE VIRTUAL TABLE IF NOT EXISTS ComplianceChunks_FTS USING fts5(
            ChunkId,
            ParentId UNINDEXED, 
            Content,
            tokenize='porter'
        );");
        }

        // Run this once before starting your ingestion
        public async Task IngestionInSQLLite(List<HybridComplianceRecord> childRecord)
        {
            using var connection = new SqliteConnection("Data Source=OmniGuard.db");
            await connection.OpenAsync();

            foreach (var child in childRecord)
            {
                // 3. Write to SQLite (Lexical/BM25)
                await connection.ExecuteAsync(@"
        INSERT INTO ComplianceChunks_FTS (ChunkId, ParentId, Content) 
        VALUES (@id, @pId, @text)",
                    new { id = child.Id, pId = child.Parent_Id, text = child.Text });
            }
        }

        // Run this once before starting your ingestion
        public async Task RetrieveFromSQLLite(List<HybridComplianceRecord> childRecord)
        {
            using var connection = new SqliteConnection("Data Source=OmniGuard.db");
            await connection.OpenAsync();

            foreach (var child in childRecord)
            {
                // 3. Write to SQLite (Lexical/BM25)
                await connection.ExecuteAsync(@"
        INSERT INTO ComplianceChunks_FTS (ChunkId, ParentId, Content) 
        VALUES (@id, @pId, @text)",
                    new { id = child.Id, pId = child.Parent_Id, text = child.Text });
            }
        }
    }
}
