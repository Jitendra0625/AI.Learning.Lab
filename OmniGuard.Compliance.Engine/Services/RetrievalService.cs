using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Pinecone;
// We need the NATIVE client for the VectorStore constructor
using NativePineconeClient = Pinecone.PineconeClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using Pinecone;
using Pinecone.Grpc;
using OneOf.Types;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace OmniGuard.Compliance.Engine.Services
{
    internal class RetrievalService
    {
        private readonly PineconeVectorStore _vectorStore;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly string _parentStorePath;
        private NativePineconeClient _client;
        private readonly IChatCompletionService _chatService;
        public RetrievalService(NativePineconeClient client, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatCompletionService chatService)
        {
            _vectorStore = new PineconeVectorStore(client);
            _embeddingGenerator = embeddingGenerator;
            _parentStorePath = Path.Combine(AppContext.BaseDirectory, "ParentStore");
            _client = client;
            _chatService = chatService;
        }
        public async Task<string> GetComplianceAnswerAsync(string userQuery, string indexName = "retail-bank-regulatory-index")
        {
            var queryEmbeddings = await _embeddingGenerator.GenerateAsync(new[] { userQuery });
            float[] queryVector = queryEmbeddings[0].Vector.ToArray();

            // 
            // We ONLY want 'child' records for the semantic match
            var searchOptions = new Pinecone.QueryRequest // This class is provided by Pincone
            {
                TopK = 10,// // Look deeper into the document . get top 5 or number given mmatchs 
                Vector = queryVector,
                IncludeMetadata = true,
                IncludeValues = true,

                Filter = new Metadata
                {
                    ["ChunkType"] = "child"
                }
            };
            // 1. Get the Index details to retrieve the Host URL
            //var indexDescription = await _client.DescribeIndexAsync(indexName);

            var index = _client.Index(indexName);
            var queryResponse = await index.QueryAsync(searchOptions);

            //As we now have small chidl context using semantic search, lets not retrive related parent record from stord in local folder in this use case
            var contextBuilder = new StringBuilder();
            var processedParent = new HashSet<string>();
            /* Please see below why I used HashSet and not List
             * 
             * With a List: You would fetch and print the same Page text 3 times. This wastes tokens, clutters the LLM's brain, and makes your console output look broken.
               With a HashSet: It ensures you only fetch and display each Parent Page exactly once, no matter how many "Child" hits it has.

                Using a HashSet instead of a List is a specific optimization for Duplicate Prevention and Speed.

            */

            /* "I implemented a de-duplication logic using a HashSet during the Parent-Expansion phase. Since multiple relevant semantic chunks often 
             * originate from the same authoritative page, using a HashSet ensures we only perform a single I/O operation to the local Document Store per page. 
             * This prevents redundant context from being fed to the LLM and optimizes the engine's performance."
             * */
            foreach (var match in queryResponse.Matches)
            {
                string parentId = match.Metadata["Parent_Id"].ToString();
                // Only fetch each parent once to avoid duplicates
                if (!processedParent.Contains(parentId))
                {
                    string cleanParentId = Regex.Unescape(parentId).Replace("\"", "");
                    string filePath = Path.Combine(_parentStorePath, $"{cleanParentId}.txt");

                    if (File.Exists(filePath))
                    {
                        string fullPageText = await File.ReadAllTextAsync(filePath);
                        contextBuilder.AppendLine($"\n[AUTHORITATIVE POLICY - PAGE {match.Metadata["PageNumber"].ToString()}]");
                        contextBuilder.AppendLine(fullPageText);
                        contextBuilder.AppendLine(new string('=', 30));
                    }
                    processedParent.Add(parentId);
                }
            }

            return contextBuilder.Length > 0
                ? contextBuilder.ToString()
                : "No matching regulatory policy found in the engine.";
        }

        public async Task<(string context, string validationReasioning)> GetJudgedContextAsync(string userQuery, string rawContext)
        {
            try
            {
                // 1. Define the Judge's Prompt
                var judgePrompt = $"""
                                    SYSTEM: You are a Bank Compliance Auditor. 
                                    TASK: Evaluate if the provided CONTEXT contains the specific answer for the USER_QUERY.
        
                                    USER_QUERY: {userQuery}
                                    CONTEXT: {rawContext}

                                    RESPONSE FORMAT:
                                    CONFIDENCE: [High/Medium/Low]
                                    REASON: [Brief explanation]
                                    """;

                // Call the LLM to to gett the confidence
                var settings = new OpenAIPromptExecutionSettings
                {
                    Temperature = 0.4F,

                };
                var response= await _chatService.GetChatMessageContentsAsync(judgePrompt, settings);
                return (rawContext, response.Count()==0? "Validation failed.":response[0].Content);
            }
            catch (Exception ex)
            {
            }
            return ("", "");

        }


        public async Task<string> GetFinalResponseAsync(string userQuery, string indexName= "retail-bank-regulatory-index")
        {
            try
            {
                var rawContext = await GetComplianceAnswerAsync(userQuery, indexName);

                // Try the AI Judge first
                var (context, reasoning) = await GetJudgedContextAsync(userQuery, rawContext);
                if (reasoning.Contains("Medium", StringComparison.OrdinalIgnoreCase))
                {
                    // High-value Senior Logic: Provide context but add a "Compliance Warning"
                    var warning = $"""
                                    COMPLIANCE ADVISORY: The engine found relevant sections regarding '{userQuery}', 
                                    but the authoritative evidence is partial. 

                                    [Judge Reasoning]: {reasoning}

                                    [Supporting Context]:
                                    {context}
                                    """;
                    return warning;
                }
                return $"[JUDGE ANALYSIS]: {reasoning}\n\n{context}";
            }
            catch (Exception ex)
            {
                // Fallback to basic logic if the Judge is offline
                Console.WriteLine($"⚠️ Judge offline: {ex.Message}");
                return await SearchWithFallbackAsync(userQuery);
            }
        }
        public async Task<string> SearchWithFallbackAsync(string userQuery)
        {

            Console.WriteLine($"⚠️ Vector Store offline: Falling back to Local Store...");

            // Fallback: Simple Keyword search in our ParentStore text files
            var localFiles = Directory.GetFiles(_parentStorePath, "*.txt");
            var bestFile = localFiles.FirstOrDefault(f => File.ReadAllText(f).Contains(userQuery, StringComparison.OrdinalIgnoreCase));// get any first file contining the part of user query

            return bestFile != null ? await File.ReadAllTextAsync(bestFile) : "No information available.";

        }

    }


}
