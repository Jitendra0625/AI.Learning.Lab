using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using Pinecone;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
// We need the NATIVE client for the VectorStore constructor
using NativePineconeClient = Pinecone.PineconeClient;

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
        public async Task<(string fullContext, List<int> pageNumbers)> GetComplianceAnswerAsync(string userQuery, string indexName = "retail-bank-regulatory-index")
        {
            var queryEmbeddings = await _embeddingGenerator.GenerateAsync(new[] { userQuery });
            float[] queryVector = queryEmbeddings[0].Vector.ToArray();
            List<int> pageNumber = new List<int>();

            // 
            // We ONLY want 'child' records for the semantic match
            var searchOptions = new Pinecone.QueryRequest // This class is provided by Pincone
            {
                TopK = 3,// // Look deeper into the document . get top 5 or number given mmatchs 
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
                        pageNumber.Add(Convert.ToInt32(match.Metadata["PageNumber"].ToString()));
                    }
                    processedParent.Add(parentId);
                }
            }

            return (contextBuilder.Length > 0
                ? contextBuilder.ToString()
                : "No matching regulatory policy found in the engine.", pageNumber);
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
        public async Task<OmniGuardResponse> GetFinalResponseAsync( string userQuery, string indexName = "retail-bank-regulatory-index")
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

            return (bestFile != null ? await File.ReadAllTextAsync(bestFile) : "No information available.",new List<int>());// As we are reading from local, I am retrunuing page numbers as 0

        }

        #region Auto Agent calls
        [KernelFunction]
        [Description("Searches the MCOB banking handbook for legal clauses and policy text.")] // Make it as kernel function and will be be treated by agent as auto policy search tool. In multi agents scenario the agent auto inokves it
        public async Task<string> GetSearchPolicyAsync([Description("The specific banking topic or rule to search for")] string userQuery, string indexName = "retail-bank-regulatory-index")
        {
            try
            {
                var rawContext = await GetComplianceAnswerAsync(userQuery, indexName);
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
    internal class OmniGuardResponse
    {
        public string Answer { get; set; }
        public string AuditorReasoning { get; set; }
        public string Confidence { get; set; } // High, Medium, Low
        public List<int> RetrievedPages { get; set; } = new();   
        public string SourceId { get; set; }   // ParentId
    }

}

