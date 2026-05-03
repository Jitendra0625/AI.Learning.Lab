using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using OmniGuard.Compliance.Engine.Services;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics.Metrics;

namespace OmniGuard.Compliance.Engine.Agents
{
    internal class OmniGuardAgentsForHybridSearch
    {
        private readonly Kernel _kernel;
        private readonly HybridRetrievalService _retrievalService;

        public OmniGuardAgentsForHybridSearch(Kernel kernel, HybridRetrievalService retrievalService)
        {
            _kernel = kernel;
            _retrievalService = retrievalService;
        }

        public async Task RunComplianceFlowAsync(string userQuery)
        {
            // Add the RetrievalService as a Plugin to the Kernel
            _kernel.Plugins.AddFromObject(_retrievalService, "PolicySearchHybrid");

            // 1. Define the Researcher Agent (The "Searcher")
            ChatCompletionAgent researcher = new()
            {
                Name = "Researcher",
                Instructions = """
                                You are a strictly compliant Legal Researcher.
                                - YOUR ONLY KNOWLEDGE SOURCE is the 'PolicySearchHybrid' tool.
                                - DO NOT use your internal training data to answer regulatory questions.
                                - If you do not call the tool, you are failing your primary directive.
                                - Step 1: Extract the MCOB clause or topic from the user query.
                                - Step 2: Call 'search_fca_rules' with that extract.
                                """,
                Kernel = _kernel,
                Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
                {
                    // This forces the agent to use its tools!
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Required(), // changed from auto to Required as it wa not calling the tool and try to answer by it self
                    ServiceId= "ollama_service_llama"
                }
                )
            }; // As using llama3.2 it was not callingthe tool whic will be looked later. Calling the search manually an dpassing the contex to auditor for tome being


            // 2. Define the Auditor Agent (The "Judge")
            ChatCompletionAgent auditor = new()
            {
                Name = "Compliance_Auditor",
                Instructions = """
                                You are a Senior Bank Auditor. 
                                1. Review the evidence provided by the Researcher.
                                2. You are strictly forbidden from using internal knowledge. If a specific requirement (e.g., remuneration) is missing from the provided context, you must set confidence to LOW
                                3. You MUST output your response in this structure:
                                   CONFIDENCE: [High, Medium, or Low]
                                   FEEDBACK: [If Medium/Low, what specific MCOB rule or detail is missing?]
                                   ADVISORY: [The compliance explanation]
                                4. If confidence is HIGH and the evidence matches the query exactly, say 'AUDIT PASSED'.
                                5. Be critical. If the text is generic and lacks the specific MCOB clause number requested, set confidence to LOW.
                                """,
                Kernel = _kernel, // Ensure this kernel has your HuggingFace service registered
                Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
                {
                    ServiceId = "HuggingFaceChat",//"ollama_service_llama",
                    //MaxTokens = 500, // Limits the length of the reasoning + verdict
                    Temperature = 0.0 // Makes it more decisive/less "loopy"
                })
            };

            // 3. Create the Group Chat
#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            AgentGroupChat chat = new(researcher, auditor)
            {
                ExecutionSettings = new()
                {
                    //TerminationStrategy = new ApprovalTerminationStrategy(), 
                    SelectionStrategy = new SequentialSelectionStrategy
                    {
                        InitialAgent = researcher
                    } // THIS FORCES THE TURN: Researcher -> Auditor
                }
            };
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            //// 4. Add the User's teamQuestion
            //chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, userQuery));
            // 1. MANUALLY RUN THE RESEARCHER ENGINE (No LLM middleman)
            Console.WriteLine("\n[SYSTEM]: Researcher is executing Hybrid RRF Search...");

            // Call your retrieval service directly
            var (context, pages) = await _retrievalService.GetComplianceAnswerAsync(userQuery);

            // 2. INJECT INTO HISTORY
            ChatHistory chatHistory = new ChatHistory();
            chatHistory.AddUserMessage(userQuery);

            // We "Act" as the Researcher and provide the evidence to the Auditor
            chatHistory.AddAssistantMessage($"I have searched the authoritative FCA handbook. Here is the evidence found:\n{context}");


            //// 5. Let them talk!
            //await foreach (var response in chat.InvokeAsync())
            //{
            //    Console.WriteLine($"\n--- {response.AuthorName?.ToUpper()} ---");
            //    Console.WriteLine(response.Content);

            //    //// Termination: If the Auditor gives a 'High' score, we are done
            //    //if (response.AuthorName == "Auditor" && response.Content.Contains("High"))
            //    //{
            //    //    Console.WriteLine("Compliance Goal Achieved.");
            //    //    break;
            //    //}
            //}

            // ok while testing as AgentGroupChat in 1.74 preview is still in evaluation stage so it's not calling the auditor after calling the researcher. May be this auto works later on when 1.74 preview package ket stable.
            // I have decided to run agents manually.

            // 2. Step 1: Force the Researcher to find the data
            //int maxRetries = 2;
            //int currentAttempt = 1;
            //bool isAuditComplete = false;

            //while (currentAttempt <= maxRetries && !isAuditComplete)
            //{
            //    Console.WriteLine($"\n[SYSTEM]: Starting Attempt {currentAttempt}...");

            //    // --- STEP 1: RESEARCHER ---
            //    Console.WriteLine("[SYSTEM]: Researcher is gathering evidence...");


            //    await foreach (var message in researcher.InvokeAsync(chatHistory))
            //    {
            //        Console.ForegroundColor = ConsoleColor.Cyan;
            //        Console.WriteLine($"\n[RESEARCHER]: {message.Message.Content}");
            //        Console.ResetColor();
            //        chatHistory.Add(message); // Add Researcher's findings to history
            //    }

            //    // --- STEP 2: AUDITOR ---
            //    Console.WriteLine("[SYSTEM]: Passing evidence to Auditor for validation...");
            //    await foreach (var message in auditor.InvokeAsync(chatHistory))
            //    {
            //        string auditorContent = message.Message.Content ?? "";
            //        chatHistory.Add(message); // Add Auditor's verdict to history

            //        // Color-coded logging based on confidence
            //        LogAuditorMessage(auditorContent);

            //        // --- STEP 3: DECISION LOGIC ---
            //        if (auditorContent.Contains("High", StringComparison.OrdinalIgnoreCase))
            //        {
            //            Console.WriteLine("\n[SUCCESS]: Compliance Goal Achieved.");
            //            isAuditComplete = true;
            //            break;
            //        }
            //        else
            //        {
            //            Console.ForegroundColor = ConsoleColor.Red;
            //            Console.WriteLine($"\n[RETRY]: Auditor found confidence too low ({currentAttempt}/{maxRetries}).");
            //            Console.ResetColor();

            //            // We add a system "nudge" to the history for the Researcher to see
            //            chatHistory.AddSystemMessage("Researcher, the Auditor is not satisfied. Please use the PolicySearch tool again, but focus on the missing details mentioned by the Auditor.");
            //            currentAttempt++;
            //        }
            //    }
            //} // not uisng agent to call too for search due to llama 3.2 and ollmana connector behavior not calling the tool. Searched the text manual and passing the context to auditor
            // Auditor Agent (using the long-timeout HttpClient we set up)
            await foreach (var message in auditor.InvokeAsync(chatHistory))
            {
                // ... your existing color-coded logging ...
                Console.ForegroundColor = ConsoleColor.Yellow;
                if (message.Message.Content.Contains("<thought>")) Console.ForegroundColor = ConsoleColor.Gray;
                LogAuditorMessage(message.Message.Content);
                
                if (message.Message.Content.Contains("High", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("\n[SUCCESS]: Compliance Goal Achieved.");
                    break;
                }
                Console.ResetColor();
            }
            //if (!isAuditComplete)
            //{
            //    Console.WriteLine("\n[FINAL]: Could not reach high confidence after maximum retries.");
            //}
        }

        private void LogAuditorMessage(string content)
        {
            if (content.Contains("High")) Console.ForegroundColor = ConsoleColor.Green;
            else if (content.Contains("Medium")) Console.ForegroundColor = ConsoleColor.Yellow;
            else Console.ForegroundColor = ConsoleColor.Red;

            Console.WriteLine($"\n[AUDITOR]: {content}");
            Console.ResetColor();
        }
    }

}


