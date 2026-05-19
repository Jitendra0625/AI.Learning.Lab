using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Omniguard.Ingestion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public class VectorBatchProcessingEngine
    {
        private readonly IStagingBatchRepository _repository;
        private readonly HttpClient _pineconeClient;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly ILogger<VectorBatchProcessingEngine> _logger;

        // The constructor injects your existing dependencies from Program.cs
        public VectorBatchProcessingEngine(
            IStagingBatchRepository repository,
            IHttpClientFactory httpClientFactory,
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            ILogger<VectorBatchProcessingEngine> logger)
        {
            _repository = repository;
            // Pulls the optimized connection pooling HttpClient configured in Program.cs
            _pineconeClient = httpClientFactory.CreateClient("PineconeBatchClient");
            _embeddingGenerator = embeddingGenerator;
            _logger = logger;
        }

        [Function("ExecuteBatchVectorIngestion")]
        public async Task Run([TimerTrigger("*/10 * * * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation("Polling Staging Buffer for pending embedding tasks...");

            // STEP 1: Fetch exactly 50 safe, locked rows via UPDLOCK / READPAST
            // If the database is empty or all items are processed, this returns 0 items and exits cleanly.
            var batch = await _repository.AllocateProcessingBatchAsync(batchSize: 50, lockDurationMinutes: 5);
            if (!batch.Any())
            {
                return;
            }

            var pineconeRequest = new PineconeUpsertRequest();
            var successIds = new List<long>();
            var failureTracking = new List<(long SequenceId, string Error)>();

            // STEP 2: Process the batch locally through your ONNX model
            foreach (var item in batch)
            {
                try
                {
                    // Generate vector values using your pre-loaded Microsoft.Extensions.AI registration
                    var embeddingResult = await _embeddingGenerator.GenerateAsync(new List<string> { item.ExtractedText });
                    var vectorValues = embeddingResult.First().Vector.ToArray().ToList();

                    // Map into the Pinecone payload schema
                    pineconeRequest.Vectors.Add(new PineconeVectorDto
                    {
                        Id = item.VectorId, // Pre-deterministic format: DocumentId#P[X]#C[Y]
                        Values = vectorValues,
                        Metadata = new Dictionary<string, object>
                        {
                            { "DocumentId", item.DocumentId },
                            { "PageNumber", item.PageNumber },
                            { "ChunkIndex", item.ChunkIndex },
                            { "ParentBlobUrl", item.ParentBlobUrl } // Anchor for Parent-Child Retrieval (PDR)
                        }
                    });

                    // Track that this specific row converted successfully
                    successIds.Add(item.SequenceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ONNX local embedding layout transformation failed for Sequence ID {Id}.", item.SequenceId);
                    // If a single row is corrupted and crashes ONNX, isolate it so it doesn't break the whole batch
                    failureTracking.Add((item.SequenceId, $"Local ONNX Core Fault: {ex.Message}"));
                }
            }

            // STEP 3: Multi-Row Transaction Batch Write directly to Pinecone Serverless
            if (pineconeRequest.Vectors.Any())
            {
                try
                {
                    // POST the 50 vectors over the network in a single payload
                    var response = await _pineconeClient.PostAsJsonAsync("vectors/upsert", pineconeRequest);

                    if (!response.IsSuccessStatusCode)
                    {
                        // If Pinecone rejects the payload (e.g., bad API key, wrong URL format), capture the exact error message
                        string apiError = await response.Content.ReadAsStringAsync();
                        throw new HttpRequestException($"Pinecone serverless edge rejected batch upload payload: {apiError}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pinecone link connection dropped or aborted. Reverting current operational batch statuses.");

                    // Network Failover: If the HTTP upload fails entirely, we must NOT mark the items as successful.
                    // We move all successful local conversions into the failure tracking list so they retry next time.
                    foreach (var id in successIds)
                    {
                        failureTracking.Add((id, $"Pinecone Target Connection Error: {ex.Message}"));
                    }
                    successIds.Clear();
                }
            }

            // STEP 4: Update Database State Records Atomically
            // This writes the final results (Completed vs. Failed) back to SQL Server in a single Dapper transaction.
            await _repository.CompleteBatchAsync(successIds, failureTracking);
            _logger.LogInformation("Batch iteration completed. Successfully Synced: {Success}, Failed/Logged: {Fail}.", successIds.Count, failureTracking.Count);
        }
    }

    // --- Pinecone JSON Payload Contracts ---

    public class PineconeUpsertRequest
    {
        [JsonPropertyName("vectors")]
        public List<PineconeVectorDto> Vectors { get; set; } = new();

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "policy-documents"; 
    }

    public class PineconeVectorDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = null!;

        [JsonPropertyName("values")]
        public List<float> Values { get; set; } = new();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}