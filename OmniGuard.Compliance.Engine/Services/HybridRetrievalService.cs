using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using OmniGuard.Compliance.Engine.Models;
using Pinecone;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq.Expressions;
// We need the NATIVE client for the VectorStore constructor
using NativePineconeClient = Pinecone.PineconeClient;
using System.Linq.Expressions;
using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;
using iText.StyledXmlParser.Jsoup.Safety;

namespace OmniGuard.Compliance.Engine.Services
{
    internal class HybridRetrievalService
    {
        private readonly PineconeVectorStore _vectorStore;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
        private readonly string _parentStorePath;
        private readonly SQLLiteService _sqlLiteDB;
        private NativePineconeClient _client;
        private readonly IChatCompletionService _chatService;
        private readonly IDbConnection _dbConnection;
        public HybridRetrievalService(NativePineconeClient client, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, IChatCompletionService chatService, SQLLiteService sqlLiteDB)
        {
            _vectorStore = new PineconeVectorStore(client);
            _embeddingGenerator = embeddingGenerator;
            _parentStorePath = Path.Combine(AppContext.BaseDirectory, "ParentStore");
            _client = client;
            _chatService = chatService;
            _dbConnection = new SqliteConnection("Data Source=OmniGuard.db");
        }
        public async Task<(string fullContext, List<int> pageNumbers)> GetComplianceAnswerAsync(string userQuery, string indexName = "retail-bank-regulatory-hybridindex")
        {
            try
            {
                var queryEmbeddings = await _embeddingGenerator.GenerateAsync(new[] { userQuery });
                float[] queryVector = queryEmbeddings[0].Vector.ToArray();
                List<int> pageNumber = new List<int>();

                // 
                // We ONLY want 'child' records for the semantic match
                var searchOptions = new VectorSearchOptions<HybridComplianceRecord> // This class is provided by Pincone
                {
                    Filter = record => record.ChunkType == "child"
                };
                var collection = _vectorStore.GetCollection<string, HybridComplianceRecord>(indexName);




                //// 1. Remove common trailing punctuation
                //char[] trimChars = { '?', '.', ',', '!', ';', ':' };

                //// 2. Sanitize and Quote
                //string sanitizedQuery = string.Join(" ", userQuery.Split(' ')
                //    .Select(word => $"\"{word.Trim(trimChars).Replace("\"", "\"\"")}\""));

                // 1.Clean the string but keep the numbers and dots together for a moment
                // 2. Wrap the whole thing in one set of quotes for a "Phrase Search"
                // 3. Replace the dots with spaces because the tokenizer sees dots as spaces anyway



                var semanticTask = collection.SearchAsync(queryVector, 10, searchOptions).ToListAsync().AsTask(); 

                string sanitizedQuery = userQuery.Replace(".", " ").Replace("?", "").Replace("-", " ");
               
                var keywordTask = _dbConnection.QueryAsync<dynamic>(
                    "SELECT ParentId, ChunkId, rank  FROM ComplianceChunks_FTS WHERE Content MATCH @query ORDER BY rank LIMIT 10",
                    new { query = sanitizedQuery });

                await Task.WhenAll(semanticTask, keywordTask);

                // 2. RRF Fusion
                var semanticList = await semanticTask;
                var keywordList = await keywordTask;

                var fusedScores = new Dictionary<string, double>();
                const int k = 60; // Standard RRF constant

                // Rank Semantic Results (By ParentId)
                for (int i = 0; i < semanticList.Count; i++)
                {
                    string pId = semanticList[i].Record.Parent_Id;
                    fusedScores[pId] = fusedScores.GetValueOrDefault(pId) + (1.0 / (k + i + 1));
                }

                // Rank Keyword Results (By ParentId)
                var kList = keywordList.ToList();
                for (int i = 0; i < kList.Count; i++)
                {
                    string pId = kList[i].ParentId;
                    fusedScores[pId] = fusedScores.GetValueOrDefault(pId) + (1.0 / (k + i + 1));
                }

                // 3. Parent Lookup (The "Bridge")
                // Get Top 3 Fused Parent IDs
                var topParentIds = fusedScores.OrderByDescending(x => x.Value)
                                              .Take(3)
                                              .Select(x => x.Key);

                // 4. Build Final Context
                var contextBuilder = new StringBuilder();
                var pageNumbers= new List<int>();
                foreach (var pId in topParentIds)
                {
                    // Load the full page .txt file you saved earlier
                    string filePath = Path.Combine(_parentStorePath, $"{pId}.txt");
                    var fullText = await File.ReadAllTextAsync(filePath);
                    contextBuilder.AppendLine($"[AUTHORITATIVE POLICY - {pId}]");
                    contextBuilder.AppendLine(fullText);
                    contextBuilder.AppendLine("---");
                    pageNumber.Add(Convert.ToInt32(pId.Split("-page-").LastOrDefault())); // Extract page number from ParentId format "parent_Page_X"
                }

                return (contextBuilder.ToString(), pageNumber);
            }
            catch (Exception ex)
            {
            }

            return ("", new List<int>(0));
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
                                    REASON: [Brief explanation one or max two lines]
                                    """;

                // Call the LLM to to gett the confidence
                var settings = new OpenAIPromptExecutionSettings
                {
                    Temperature = 0.4F,

                };
                var response = await _chatService.GetChatMessageContentsAsync(judgePrompt, settings);
                return (rawContext, response.Count() == 0 ? "Validation failed." : response[0].Content);
            }
            catch (Exception ex)
            {
            }
            return ("", "");

        }


        /// <summary>
        /// This is the method which is like normal fucntion call and then calling another fucntion  Judge to get the confidence. For multiagent we are not calling GetJudgedContextAsync in GetSearchPolicyAsync 
        /// Rhe researche agent will auto call the kernel fucntion GetSearchPolicyAsync will use the context and then pass the context to auditor agent to get the confidenc on response.
        /// This is Linear RAG Pipeline The Concept: A "Scripted" or "Hard-coded" workflow.Structure: A single service executes steps in a fixed order (Search->Judge->Respond).
        /// The New Approach: "Agentic Orchestration". The Concept: A "Dynamic" or "Autonomous" workflow.
        /// Structure: Multiple specialized agents(Researcher & Auditor) collaborate in a shared state(the Chat).
        /// </summary>
        /// <param name="userQuery"></param>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public async Task<OmniGuardResponse> GetFinalResponseAsync(string userQuery, string indexName = "retail-bank-regulatory-hybridindex")
        {
            try
            {
                var (rawContext, pageNumber) = await GetComplianceAnswerAsync(userQuery, indexName);

                // Try the AI Judge first
                var (context, reasoning) = await GetJudgedContextAsync(userQuery, rawContext);
                //if (reasoning.Contains("Medium", StringComparison.OrdinalIgnoreCase))
                //{
                //    // High-value Senior Logic: Provide context but add a "Compliance Warning"
                //    var warning = $"""
                //                    COMPLIANCE ADVISORY: The engine found relevant sections regarding '{userQuery}', 
                //                    but the authoritative evidence is partial. 

                //                    [Judge Reasoning]: {reasoning}

                //                    [Supporting Context]:
                //                    {context}
                //                    """;
                //    return warning;
                //}
                //return $"[JUDGE ANALYSIS]: {reasoning}\n\n{context}";

                string confidence = "Low";
                if (reasoning.Contains("High", StringComparison.OrdinalIgnoreCase)) confidence = "High";
                else if (reasoning.Contains("Medium", StringComparison.OrdinalIgnoreCase)) confidence = "Medium";

                return new OmniGuardResponse
                {
                    Answer = context,
                    AuditorReasoning = reasoning,
                    Confidence = confidence,
                    RetrievedPages = pageNumber,
                    //  ParentId = rawContext.ParentId
                };
            }
            catch (Exception ex)
            {
                // Fallback to basic logic if the Judge is offline
                Console.WriteLine($"Judge offline: {ex.Message}");
                var result = await SearchWithFallbackAsync(userQuery);
                return new OmniGuardResponse
                {
                    Answer = result.fullText,
                    AuditorReasoning = string.Empty,
                    Confidence = string.Empty,
                    RetrievedPages = result.pageNumber,
                    //  ParentId = rawContext.ParentId
                };
            }
        }
        public async Task<(string fullText, List<int> pageNumber)> SearchWithFallbackAsync(string userQuery)
        {

            Console.WriteLine($"Vector Store offline: Falling back to Local Store...");

            // Fallback: Simple Keyword search in our ParentStore text files
            var localFiles = Directory.GetFiles(_parentStorePath, "*.txt");
            var bestFile = localFiles.FirstOrDefault(f => File.ReadAllText(f).Contains(userQuery, StringComparison.OrdinalIgnoreCase));// get any first file contining the part of user query

            return (bestFile != null ? await File.ReadAllTextAsync(bestFile) : "No information available.", new List<int>());// As we are reading from local, I am retrunuing page numbers as 0

        }

        #region Auto Agent calls
        [KernelFunction("search_fca_rules")]
        [Description("Search the FCA MCOB handbook for specific rules.")]// Make it as kernel function and will be be treated by agent as auto policy search tool. In multi agents scenario the agent auto inokves it
        public async Task<string> GetSearchPolicyAsync([Description("The specific regulatory question or MCOB clause ID")] string query)
        {
            try
            {
                // Add this to see exactly what is arriving from the LLM
                Console.WriteLine($"\n[DEBUG]: LLM sent to tool: {query}");
                var rawContext = await GetComplianceAnswerAsync(query);
                //Multi agent will call this GetSearchPolicyAsync first and then the Auditor agent in OmniGuardAgant class will the instruction given to find the confidence.

                //// Try the AI Judge first
                //var (context, reasoning) = await GetJudgedContextAsync(userQuery, rawContext);
                //if (reasoning.Contains("Medium", StringComparison.OrdinalIgnoreCase))
                //{
                //    // High-value Senior Logic: Provide context but add a "Compliance Warning"
                //    var warning = $"""
                //                    COMPLIANCE ADVISORY: The engine found relevant sections regarding '{userQuery}', 
                //                    but the authoritative evidence is partial. 

                //                    [Judge Reasoning]: {reasoning}

                //                    [Supporting Context]:
                //                    {context}
                //                    """;
                //    return warning;
                //}
                return rawContext.fullContext;
            }
            catch (Exception ex)
            {
                //// Fallback to basic logic if the Judge is offline
                //Console.WriteLine($"Judge offline: {ex.Message}");
                //return await SearchWithFallbackAsync(userQuery);
            }
            return string.Empty;
        }
        #endregion

    }

}

