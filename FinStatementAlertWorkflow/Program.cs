using System;
using System.ClientModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FinStatementAlertWorkflow
{
    // ==========================================
    // 1. DEFINE STRONGLY-TYPED DATA MODELS
    // ==========================================
    public class BankTransaction
    {
        public DateTime Date { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    class Program
    {
        // Global clients used by the RAG/Parsing tools
        private static BlobContainerClient? containerClient;
        private static DocumentIntelligenceClient? docIntelClient;

        static async Task Main(string[] args)
        {
            // ==========================================
            // 2. CONFIGURATION & INFRASTRUCTURE KEYS
            // ==========================================
            var openAiEndpoint = new Uri("");
            var openAiKey = new ApiKeyCredential("");

            var blobConnectionString = "DefaultEndpointsProtocol=https;AccountName=;AccountKey===;EndpointSuffix=core.windows.net";

            var docIntelEndpoint = new Uri("");
            var docIntelKey = new AzureKeyCredential("");

            var appInsightsConnectionString = "";

            // ==========================================
            // 3. INITIALIZE SECURE CLIENTS
            // ==========================================
            // Storage Connection
            containerClient = new BlobContainerClient(blobConnectionString, "bank-statements");
            await containerClient.CreateIfNotExistsAsync();

            // Document Intelligence Connection
            docIntelClient = new DocumentIntelligenceClient(docIntelEndpoint, docIntelKey);

            // OpenAI Connection & OpenTelemetry Wrapping
            var openAiClient = new AzureOpenAIClient(openAiEndpoint, openAiKey);
            IChatClient baseChatClient = openAiClient.GetChatClient("chatmodel").AsIChatClient();

            IChatClient chatClient = baseChatClient
                .AsBuilder()
                .UseOpenTelemetry(
                    sourceName: "Experimental.Microsoft.Extensions.AI",
                    configure: options => options.EnableSensitiveData = true
                )
                .Build();

            // ==========================================
            // 4. OPENTELEMETRY PROVIDERS
            // ==========================================
            using var tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Microsoft.Agents.AI")
                .AddAzureMonitorTraceExporter(options => options.ConnectionString = appInsightsConnectionString)
                .Build();

            using var meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter("Microsoft.Agents.AI")
                .AddAzureMonitorMetricExporter(options => options.ConnectionString = appInsightsConnectionString)
                .Build();

            // ==========================================
            // 5. DEFINE NATIVE CORE TOOLS
            // ==========================================
            [Description("Downloads a financial bank statement PDF from Azure Blob Storage and extracts text and transaction tables.")]
            async Task<string> DownloadAndParsePdfAsync([Description("The exact name of the PDF file in the blob container (e.g., july-statement.pdf).")] string blobName)
            {
                if (containerClient == null || docIntelClient == null)
                    return "Error: Infrastructure clients are not initialized.";

                Console.WriteLine($"\n[System]: Locating '{blobName}' in Azure Blob Storage...");
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                {
                    return $"Error: The bank statement '{blobName}' was not found in the storage container.";
                }

                Console.WriteLine("[System]: Downloading blob and triggering Azure AI Document Intelligence...");
                var blobDownload = await blobClient.DownloadContentAsync();
                BinaryData documentData = blobDownload.Value.Content;

                var operation = await docIntelClient.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", documentData);
                var result = operation.Value;

                Console.WriteLine("[System]: PDF structural extraction complete.");
                return result.Content;
            }

            var pdfDownloadAndParseTool = AIFunctionFactory.Create(DownloadAndParsePdfAsync);

            // ==========================================
            // 6. INITIALIZE AGENTS
            // ==========================================
            var statementParserAgent = chatClient.AsAIAgent(
                name: "StatementParserAgent",
                instructions: @"You are a financial parsing specialist. The user will provide a filename of a bank statement.
                                Use your tool to download and read the PDF. Extract the current 'Available Balance' and list all individual transaction lines.
                                Output your extracted findings clearly so the downstream agents can process the numbers.",
                tools: [pdfDownloadAndParseTool]
            );

            var balanceEvaluationAgent = chatClient.AsAIAgent(
                name: "BalanceEvaluationAgent",
                instructions: @"You are a financial risk analyst. Review the parsed statement text provided by the previous agent.
                                Identify if the final 'Available Balance' has dropped below the threshold of £100.
                                If it is below £100, analyze the transaction list to pinpoint the specific high-value expenses or patterns that caused the dip.
                                Append 'Status: TRIGGER_ALERT' if the balance is under £100, or 'Status: SAFE_BALANCE' if it is not."
            );

            var draftingAgent = chatClient.AsAIAgent(
                name: "DraftingAgent",
                instructions: @"You are an automated communications assistant. Review the evaluation analysis.
                                If the status is TRIGGER_ALERT, draft a direct, polite, yet urgent SMS/Email notification alert to the user. 
                                Highlight their current balance and explicitly name the high-value transactions that caused it.
                                Conclude your response explicitly with the tag: '[Action: SEND_ALERT]'.
                                If the balance is safe, draft a simple processing confirmation and conclude with: '[Action: DISMISS_LOG]'."
            );

            // ==========================================
            // 7. BUILD THE WORKFLOW GRAPH
            // ==========================================
            var dispatchAlertExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
                Console.WriteLine($"\n📱 [Outbound Alert Sent to User]:\n{messages.LastOrDefault()?.Text}")).BindAsExecutor("DispatchAlert");

            var logAndDismissExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
                Console.WriteLine($"\n📁 [Workflow Dismissed]: Account balance is healthy. Logged transaction trace.")).BindAsExecutor("LogAndDismiss");

            var builder = new WorkflowBuilder(statementParserAgent);

            builder.AddEdge(statementParserAgent, balanceEvaluationAgent);
            builder.AddEdge(balanceEvaluationAgent, draftingAgent);

            // Conditional Routing based on the Drafting Agent's concluding tag
            builder.AddEdge<IReadOnlyList<ChatMessage>>(
                source: draftingAgent,
                target: dispatchAlertExec,
                condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: SEND_ALERT]") == true
            );

            builder.AddEdge<IReadOnlyList<ChatMessage>>(
                source: draftingAgent,
                target: logAndDismissExec,
                condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: DISMISS_LOG]") == true
            );

            var workflow = builder.Build();

            // ==========================================
            // 8. EXECUTION & STREAMING SEQUENCE
            // ==========================================
            Console.WriteLine("\n=============================================");
            Console.WriteLine("Starting Financial Statement Alert Workflow");
            Console.WriteLine("=============================================\n");

            // Example input: Expects a file with this name to exist inside your blob container
            var userText = "Please process my latest statement file: july-statement.pdf";
            var startMessage = new ChatMessage(ChatRole.User, userText);

            Console.WriteLine($"[User Input Request]: {userText}\n");

            await using var run = await InProcessExecution.RunStreamingAsync(workflow, startMessage);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            // Ultimate Observability Diagnostic Loop
            await foreach (var evt in run.WatchStreamAsync())
            {
                if (evt is AgentResponseUpdateEvent update)
                {
                    Console.Write(update.Update.Text);
                }
                else if (evt is ExecutorInvokedEvent invoked)
                {
                    Console.WriteLine($"\n\n [Starting Node]: {invoked.ExecutorId}...");
                }
                else if (evt is ExecutorCompletedEvent completed)
                {
                    Console.WriteLine($"\n [Finished Node]: {completed.ExecutorId}");
                }
                else if (evt is ExecutorFailedEvent failed)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[NODE CRASHED - {failed.ExecutorId}]: {failed.Data}");
                    Console.ResetColor();
                }
                else if (evt is WorkflowErrorEvent wfError)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[WORKFLOW CRASHED]: {wfError.Exception.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine("\n\n=============================================");
            Console.WriteLine("Financial Workflow Execution Complete");
            Console.WriteLine("=============================================\n");
            Console.ReadLine();
        }
    }
}
