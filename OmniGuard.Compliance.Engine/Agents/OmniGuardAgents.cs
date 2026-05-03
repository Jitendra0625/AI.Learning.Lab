using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using OmniGuard.Compliance.Engine.Services;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics.Metrics;

namespace OmniGuard.Compliance.Engine.Agents
{
    internal class OmniGuardAgents
    {
        private readonly Kernel _kernel;
        private readonly RetrievalService _retrievalService;

        public OmniGuardAgents(Kernel kernel, RetrievalService retrievalService)
        {
            _kernel = kernel;
            _retrievalService = retrievalService;
        }

        public async Task RunComplianceFlowAsync(string userQuery)
        {
            // Add the RetrievalService as a Plugin to the Kernel
            _kernel.Plugins.AddFromObject(_retrievalService, "PolicySearch");

            // 1. Define the Researcher Agent (The "Searcher")
            ChatCompletionAgent researcher = new()
            {
                Name = "Researcher",
                Instructions = """
                                You are a Legal Researcher.
                                1.Use the PolicySearch tool to find data.
                                2.Once you have the results,summarize them clearly for the Compliance_Auditor.
                                3.You MUST provide a response after the tool call is finished.
                                """,
                Kernel = _kernel,
                Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
                {
                    // This forces the agent to use its tools!
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                    ServiceId = "HuggingFaceChat"
                }
                )
            };


            // 2. Define the Auditor Agent (The "Judge")
            ChatCompletionAgent auditor = new()
            {
                Name = "Compliance_Auditor",
                Instructions = """
                                You are a Senior Bank Auditor. 
                                1. Review the evidence provided by the Researcher.
                                2. Assign a Confidence Score: [High/Medium/Low].
                                3. Provide a 'Compliance Advisory' if the evidence is not 100% clear.
                                4. If confidence is HIGH, say 'AUDIT PASSED'.
                                """,
                Kernel = _kernel // Ensure this kernel has your HuggingFace service registered
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

            // 4. Add the User's teamQuestion
            chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, userQuery));

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
            Console.WriteLine("\n[SYSTEM]: Researcher is starting discovery...");
            var historyForResearcher = await chat.GetChatMessagesAsync().ToListAsync();
            await foreach (var message in researcher.InvokeAsync(historyForResearcher))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n[RESEARCHER]: {message.Message}");
                Console.ResetColor();

            }

            // 3. Step 2: Force the Auditor to review the Researcher's message
            Console.WriteLine("\n[SYSTEM]: Passing evidence to Auditor for validation...");
            // Refresh history so Auditor sees the Researcher's new message
            var historyForAuditor = await chat.GetChatMessagesAsync().ToListAsync();
            await foreach (var message in auditor.InvokeAsync(historyForAuditor))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                if (message.Message.Content.Contains("High")) Console.ForegroundColor = ConsoleColor.Green;
                if (message.Message.Content.Contains("Low")) Console.ForegroundColor = ConsoleColor.Red;
                if (message.Message.Content.Contains("Medium")) Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[AUDITOR]: {message.Message.Content}");


                // 4. Handle the "High/Low" logic here
                if (message.Message.Content.Contains("High"))
                    Console.WriteLine("\nCompliance Goal Achieved: Authoritative match confirmed.");
                Console.ResetColor();
            }
        }

    }

}
