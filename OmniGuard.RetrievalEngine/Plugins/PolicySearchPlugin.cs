using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // Added for production API logging
using Microsoft.SemanticKernel;
using OmniGuard.RetrievalEngine.Models;

namespace OmniGuard.RetrievalEngine.Plugins;

public class PolicySearchPlugin(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger<PolicySearchPlugin> logger) // Primary constructor logging injection
{
    [KernelFunction, Description("Searches the FCA Handbook policy database using a hybrid dense-vector and sparse-keyword matching engine to retrieve raw source context text.")]
    public async Task<string> SearchPolicyKnowledgebase(
        [Description("The explicit compliance question or regulatory terms to search for (e.g., 'MCOB 2.2.6R')")] string searchQuery)
    {
        try
        {
            // 1. Evaluate Query Intent Strategy Gate
            bool hasKeywords = ShouldExecuteSqlKeywordSearch(searchQuery);

            // 2. Generate local 384-dimension vector embedding matching the production BGE configuration
            var embeddingResult = await embeddingGenerator.GenerateAsync(searchQuery);
            float[] queryVector = embeddingResult.Vector.ToArray();

            // 3. Dispatch parallel hybrid retrieval loops (Only hit SQL FTS if structural whitelisted tags match)
            var pineconeTask = GetPineconeMatchesAsync(httpClientFactory.CreateClient("PineconeClient"), configuration, queryVector);
            var sqlFtsTask = hasKeywords
                ? GetSqlFtsMatchesAsync(configuration.GetConnectionString("SqlDatabase")!, searchQuery)
                : Task.FromResult(new List<SearchMatchDto>());

            await Task.WhenAll(pineconeTask, sqlFtsTask);
            var denseMatches = await pineconeTask;
            var sparseMatches = await sqlFtsTask;

            // 4. Perform Reciprocal Rank Fusion Blending with Metadata Aggregation
            var rrfMap = new Dictionary<string, RrfScoreTracker>();
            const float rrfConstant = 60f;

            // Populate initial weights and URLs directly from Pinecone Metadata payloads
            for (int i = 0; i < denseMatches.Count; i++)
            {
                var match = denseMatches[i];
                rrfMap[match.Id] = new RrfScoreTracker
                {
                    DeterministicId = match.Id,
                    DenseScore = 1f / (rrfConstant + (i + 1)),
                    ParentBlobUrl = match.Metadata?.ParentBlobUrl ?? string.Empty
                };
            }

            // Populate initial weights and fallback values from Sparse Keyword results
            for (int i = 0; i < sparseMatches.Count; i++)
            {
                var match = sparseMatches[i];
                if (!rrfMap.TryGetValue(match.VectorId, out var tracker))
                {
                    tracker = new RrfScoreTracker { DeterministicId = match.VectorId };
                    rrfMap[match.VectorId] = tracker;
                }
                tracker.RowHash = match.RowHash;
                if (string.IsNullOrEmpty(tracker.ParentBlobUrl)) tracker.ParentBlobUrl = match.ParentBlobUrl;
                tracker.SparseScore = 1f / (rrfConstant + (i + 1));
            }

            // ==========================================
            // ANTI-HALLUCINATION ENFORCEMENT FILTER
            // ==========================================
            var verifiedHits = new List<RrfScoreTracker>();

            foreach (var tracker in rrfMap.Values)
            {
                // CRITICAL SCENARIO: Found in Vector, but completely missed by local SQL BM25 keywords
                if (tracker.DenseScore > 0 && tracker.SparseScore == 0)
                {
                    if (hasKeywords)
                    {
                        bool isExplicitRuleQuery = searchQuery.Contains("MCOB", StringComparison.OrdinalIgnoreCase) ||
                                                   searchQuery.Contains("COBS", StringComparison.OrdinalIgnoreCase) ||
                                                   searchQuery.Contains("CASS", StringComparison.OrdinalIgnoreCase) ||
                                                   searchQuery.Contains("PRIN", StringComparison.OrdinalIgnoreCase) ||
                                                   searchQuery.Contains("SYSC", StringComparison.OrdinalIgnoreCase);

                        if (isExplicitRuleQuery)
                        {
                            // DISQUALIFY: If the user asked for a specific rule, and the active database keyword catalog missed it, drop it.
                            continue;
                        }
                    }

                    // Soft Penalty: Reduce vector contribution by 50% for purely conversational segments
                    tracker.DenseScore *= 0.5f;
                }

                verifiedHits.Add(tracker);
            }

            // Sort out the top 3 verified, non-hallucinated results
            var topBlendedHits = verifiedHits.OrderByDescending(x => x.CombinedRrfScore).Take(3).ToList();
            if (topBlendedHits.Count == 0) return "Zero verified compliance matches found across active handbook channels.";

            // 5. Download and aggregate text from Azurite Blobs using direct in-memory URL mapping
            var blobUrlsToFetch = new HashSet<string>();
            foreach (var node in topBlendedHits)
            {
                if (!string.IsNullOrEmpty(node.ParentBlobUrl))
                {
                    blobUrlsToFetch.Add(node.ParentBlobUrl);
                }
            }

            if (blobUrlsToFetch.Count == 0) return "Zero verified compliance matches found across active handbook channels.";

            var contextBuilder = new StringBuilder();
            string azuriteConnectionString = configuration.GetConnectionString("AzuriteStorage")
         ?? "UseDevelopmentStorage=true";

            var blobServiceClient = new BlobServiceClient(azuriteConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("policy-parent-pages");

            int idx = 1;

            foreach (var url in blobUrlsToFetch)
            {
                string blobName = Path.GetFileName(new Uri(url).LocalPath);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var memoryStream = new MemoryStream();
                await blobClient.DownloadToAsync(memoryStream);
                memoryStream.Position = 0;

                contextBuilder.AppendLine($"--- START OF COMPLIANCE CONTEXT DOC #{idx} (Source: {blobName}) ---");

                try
                {
                    // Open the raw binary download stream as an active PDF document structure
                    using (var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(memoryStream))
                    {
                        foreach (var page in pdfDocument.GetPages())
                        {
                            // Natively extract the hidden compliance text strings from the page
                            contextBuilder.AppendLine(page.Text);
                        }
                    }
                }
                catch (Exception pdfEx)
                {
                    logger.LogWarning(pdfEx, "Failed to parse stream as a standard PDF. Falling back to raw stream dump.");
                    memoryStream.Position = 0;
                    using var fallbackReader = new StreamReader(memoryStream);
                    contextBuilder.AppendLine(await fallbackReader.ReadToEndAsync());
                }

                contextBuilder.AppendLine($"--- END OF COMPLIANCE CONTEXT DOC #{idx} ---\n");
                idx++;
            }

            return contextBuilder.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred during hybrid framework policy execution loop.");
            return $"Error during hybrid policy execution: {ex.Message}";
        }
    }

    private bool ShouldExecuteSqlKeywordSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var regulatoryPrefixes = new[] { "MCOB", "COBS", "PRIN", "SYSC", "COND", "APER", "FIT", "CASS" };
        bool containsPrefix = regulatoryPrefixes.Any(p => query.Contains(p, StringComparison.OrdinalIgnoreCase));
        bool containsRuleNumbers = Regex.IsMatch(query, @"\b\d+\.\d+\b");
        bool containsStructuralWords = query.Contains("rule", StringComparison.OrdinalIgnoreCase)
                                    || query.Contains("section", StringComparison.OrdinalIgnoreCase);

        return containsPrefix || containsRuleNumbers || containsStructuralWords;
    }

    private async Task<List<PineconeMatch>> GetPineconeMatchesAsync(HttpClient client, IConfiguration config, float[] vector)
    {
        var payload = new PineconeQueryRequest(config["Pinecone:Namespace"]!, vector, TopK: 20);
        var response = await client.PostAsJsonAsync("/query", payload);
        if (!response.IsSuccessStatusCode) return [];
        var result = await response.Content.ReadFromJsonAsync<PineconeQueryResponse>();
        return result?.Matches ?? [];
    }

    private async Task<List<SearchMatchDto>> GetSqlFtsMatchesAsync(string connectionString, string searchTerms)
    {
        using var connection = new SqlConnection(connectionString);

        // 1. Isolate the core rule block coordinate pattern (e.g., "MCOB 4.6" or "COBS 3.5")
        // This looks for a handbook acronym followed immediately by a number sequence
        var match = Regex.Match(searchTerms, @"\b(MCOB|COBS|CASS|PRIN|SYSC)\s+\d+\.\d+[A-Za-z]?\b", RegexOptions.IgnoreCase);

        string finalFtsQueryString;

        if (match.Success)
        {
            // Force SQL Server to search for the literal exact phrase together with a trailing wildcard
            // Evaluates exactly to: "MCOB 4.6*"
            finalFtsQueryString = $"\"{match.Value.ToUpper()}*\"";
        }
        else
        {
            // Fallback: If no explicit rule coordinates are found, search for the individual words using OR
            var cleanWords = Regex.Replace(searchTerms, @"[^\w\s]", "")
                                   .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(w => $"\"{w}*\"");
            finalFtsQueryString = string.Join(" OR ", cleanWords);
        }

        // Log the exact string being sent to the database for transparent API tracking
        logger.LogInformation("Generated production FTS query payload: {FtsQuery}", finalFtsQueryString);

        // 2. High-performance relational query matching your exact database schema
        var sql = @"
        SELECT TOP 20 
            buf.VectorId, 
            buf.RowHash, 
            KEY_TBL.[RANK] AS [Rank], 
            buf.ParentBlobUrl 
        FROM CONTAINSTABLE(dbo.StagingIngestionBuffer, ExtractedText, @SearchQuery) AS KEY_TBL 
        INNER JOIN dbo.StagingIngestionBuffer AS buf 
            ON KEY_TBL.[KEY] = buf.SequenceId 
        ORDER BY KEY_TBL.[RANK] DESC";

        var results = await connection.QueryAsync<SearchMatchDto>(sql, new { SearchQuery = finalFtsQueryString });
        return results.ToList();
    }
}
