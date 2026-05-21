using System.Net.Http.Json;
using Azure.Storage.Blobs;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OmniGuard.RetrievalEngine.Models;

namespace OmniGuard.RetrievalEngine.Agents;

public class ResearcherAgent(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public async Task<ResearcherOutput> ExecuteHybridSearchAsync(AnalyzerOutput analyzerData, string rawPrompt)
    {
        var pineconeTask = GetPineconeMatchesAsync(httpClientFactory.CreateClient("PineconeClient"), analyzerData.QueryVector);
        var sqlFtsTask = GetSqlFtsMatchesAsync(configuration.GetConnectionString("SqlDatabase")!, analyzerData.FormattedSqlQuery);

        await Task.WhenAll(pineconeTask, sqlFtsTask);

        var denseMatches = await pineconeTask;
        var sparseMatches = await sqlFtsTask;

        var rrfMap = new Dictionary<string, RrfScoreTracker>(capacity: denseMatches.Count + sparseMatches.Count);
        const float rrfConstant = 60f;

        for (int i = 0; i < denseMatches.Count; i++)
        {
            var match = denseMatches[i];
            rrfMap[match.Id] = new RrfScoreTracker { DeterministicId = match.Id, DenseScore = 1f / (rrfConstant + (i + 1)) };
        }

        for (int i = 0; i < sparseMatches.Count; i++)
        {
            var match = sparseMatches[i];
            if (!rrfMap.TryGetValue(match.VectorId, out var tracker))
            {
                tracker = new RrfScoreTracker { DeterministicId = match.VectorId };
                rrfMap[match.VectorId] = tracker;
            }
            tracker.RowHash = match.RowHash;
            tracker.SparseScore = 1f / (rrfConstant + (i + 1));
        }

        var sparseBlocks = sparseMatches.Select(x => new AgentContextBlock(x.VectorId, x.RowHash, x.ParentBlobUrl)).ToList();
        return new ResearcherOutput(rrfMap.Values.ToList(), sparseBlocks);
    }

    public async Task<(int UniquePagesCount, string CombinedContextText)> ExtractParentBlobContextAsync(List<RrfScoreTracker> winningNodes, List<AgentContextBlock> sparseMatches)
    {
        var blobUrlsToFetch = new HashSet<string>();
        foreach (var node in winningNodes)
        {
            string? blobUrl = sparseMatches.FirstOrDefault(x => x.DeterministicId == node.DeterministicId || x.RowHash.SequenceEqual(node.RowHash))?.ParentBlobUrl;

            if (string.IsNullOrEmpty(blobUrl))
            {
                using var connection = new SqlConnection(configuration.GetConnectionString("SqlDatabase"));
                var record = await connection.QueryFirstOrDefaultAsync<SearchMatchDto>(
                    @"SELECT TOP 1 ParentBlobUrl 
                      FROM dbo.StagingIngestionBuffer 
                      WHERE RowHash = @RowHash OR VectorId = @VectorId",
                    new { RowHash = node.RowHash, VectorId = node.DeterministicId }
                );
                blobUrl = record?.ParentBlobUrl;
            }
            if (!string.IsNullOrEmpty(blobUrl)) blobUrlsToFetch.Add(blobUrl);
        }

        var contextBuilder = new System.Text.StringBuilder();
        int idx = 1;
        foreach (var url in blobUrlsToFetch)
        {
            var blobClient = new BlobClient(new Uri(url));
            using var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);

            contextBuilder.AppendLine($"--- START OF COMPLIANCE CONTEXT DOC #{idx} (Source: {url}) ---");
            contextBuilder.AppendLine(await reader.ReadToEndAsync());
            contextBuilder.AppendLine($"--- END OF COMPLIANCE CONTEXT DOC #{idx} ---\n");
            idx++;
        }

        return (blobUrlsToFetch.Count, contextBuilder.ToString());
    }

    private async Task<List<PineconeMatch>> GetPineconeMatchesAsync(HttpClient client, float[] vector)
    {
        var payload = new PineconeQueryRequest(configuration["Pinecone:Namespace"]!, vector, TopK: 20);
        var response = await client.PostAsJsonAsync("/query", payload);
        if (!response.IsSuccessStatusCode) return [];
        var result = await response.Content.ReadFromJsonAsync<PineconeQueryResponse>();
        return result?.Matches ?? [];
    }

    private async Task<List<SearchMatchDto>> GetSqlFtsMatchesAsync(string connectionString, string searchTerms)
    {
        using var connection = new SqlConnection(connectionString);
        var sql = @"SELECT TOP 20 
                        buf.VectorId, 
                        KEY_TBL.[KEY] as RowHash, 
                        KEY_TBL.[RANK] as Rank, 
                        buf.ParentBlobUrl 
                    FROM CONTAINSTABLE(dbo.StagingIngestionBuffer, ExtractedText, @SearchQuery) AS KEY_TBL 
                    INNER JOIN dbo.StagingIngestionBuffer AS buf ON KEY_TBL.[KEY] = buf.RowHash 
                    ORDER BY KEY_TBL.[RANK] DESC";

        var results = await connection.QueryAsync<SearchMatchDto>(sql, new { SearchQuery = searchTerms });
        return results.ToList();
    }
}