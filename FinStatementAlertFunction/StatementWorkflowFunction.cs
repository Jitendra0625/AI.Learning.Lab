using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
//using static Google.Protobuf.Collections.MapField<TKey, TValue>;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace FinStatementAlertWorkflow
{
    public class StatementWorkflowFunction
    {
        private readonly ILogger<StatementWorkflowFunction> _logger;
        private readonly IChatClient _chatClient;
        private readonly BlobContainerClient _containerClient;
        private readonly DocumentIntelligenceClient _docIntelClient;

        // Dependency Injection grabs the clients we registered in Program.cs
        public StatementWorkflowFunction(
            ILogger<StatementWorkflowFunction> logger,
            IChatClient chatClient,
            BlobContainerClient containerClient,
            DocumentIntelligenceClient docIntelClient)
        {
            _logger = logger;
            _chatClient = chatClient;
            _containerClient = containerClient;
            _docIntelClient = docIntelClient;
        }

        [Function("ProcessStatementBlob")]
        public async Task Run(
            [BlobTrigger("bank-statements/{name}", Source = BlobTriggerSource.EventGrid, Connection = "AzureWebJobsStorage")] Stream stream,
            string name)
        {
            _logger.LogInformation($"C# Blob trigger fired! Processing statement: {name}");

            // ==========================================
            // 1. DEFINE NATIVE CORE TOOLS
            // ==========================================
            // NOTE: This tool takes NO parameters. Previously it accepted a
            // `blobName` argument that the LLM had to re-derive from the prompt
            // text ("...my latest statement file: {name}"). Any deviation the
            // model introduced when echoing that filename back (case, quotes,
            // extension, punctuation) produced a blob name that didn't exist,
            // causing an immediate Azure.RequestFailedException (404 BlobNotFound).
            // Closing over `name` directly removes that entire failure mode —
            // the model no longer needs to supply or reconstruct the filename.
            [Description("Downloads the current financial bank statement PDF from Azure Blob Storage and extracts text and transaction tables.")]
            async Task<string> DownloadAndParsePdfAsync()
            {
                _logger.LogInformation($"[System]: Locating '{name}' in Blob Storage...");
                try
                {
                    BlobClient blobClient = _containerClient.GetBlobClient(name);
                    var blobDownload = await blobClient.DownloadContentAsync();

                    var analyzeRequest = new AnalyzeDocumentOptions(
     "prebuilt-layout",
     blobDownload.Value.Content
 );

                    var operation = await _docIntelClient.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeRequest);
                    _logger.LogInformation("[System]: PDF structural extraction complete.");
                    return operation.Value.Content;
                }
                catch (Azure.RequestFailedException ex)
                {
                    _logger.LogError(ex, "Failed processing blob '{BlobName}'. Status: {Status}, ErrorCode: {ErrorCode}, Message: {Message}",
                        name, ex.Status, ex.ErrorCode, ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    // Catch-all so any non-Azure exception (e.g. malformed PDF,
                    // unexpected null, etc.) also gets fully logged instead of
                    // surfacing only as a bare exception type in telemetry.
                    _logger.LogError(ex, "Unexpected error processing blob '{BlobName}': {Message}", name, ex.Message);
                    throw;
                }
            }
            var pdfTool = AIFunctionFactory.Create(DownloadAndParsePdfAsync);

            // ==========================================
            // 2. INITIALIZE AGENTS
            // ==========================================
            var statementParserAgent = _chatClient.AsAIAgent(
                name: "StatementParserAgent",
                instructions: @"You are a financial parsing specialist. Use your tool to download and read the current statement PDF.
                                Extract the 'Available Balance' and list all transactions over £50.",
                tools: [pdfTool]
            );

            var balanceEvaluationAgent = _chatClient.AsAIAgent(
                name: "BalanceEvaluationAgent",
                instructions: @"Evaluate if the extracted balance is below £100. Pinpoint high-value expenses causing the drop.
                                Append 'Status: TRIGGER_ALERT' if below £100, or 'Status: SAFE_BALANCE' if it is not."
            );

            var draftingAgent = _chatClient.AsAIAgent(
                name: "DraftingAgent",
                instructions: @"If status is TRIGGER_ALERT, draft a notification explicitly naming the high-value transactions. Conclude with: '[Action: SEND_ALERT]'.
                                If safe, output a processing confirmation and conclude with: '[Action: DISMISS_LOG]'."
            );

            // ==========================================
            // 3. BUILD THE GRAPH & EXECUTION ACTIONS
            // ==========================================
            var dispatchAlertExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
                _logger.LogWarning($"[Outbound Alert Sent to User]:\n{messages.LastOrDefault()?.Text}")).BindAsExecutor("DispatchAlert");

            var logAndDismissExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
                _logger.LogInformation($"[Workflow Dismissed]: Account balance is healthy.")).BindAsExecutor("LogAndDismiss");

            var builder = new WorkflowBuilder(statementParserAgent);
            builder.AddEdge(statementParserAgent, balanceEvaluationAgent);
            builder.AddEdge(balanceEvaluationAgent, draftingAgent);

            builder.AddEdge<IReadOnlyList<ChatMessage>>(
                source: draftingAgent, target: dispatchAlertExec,
                condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: SEND_ALERT]") == true
            );

            builder.AddEdge<IReadOnlyList<ChatMessage>>(
                source: draftingAgent, target: logAndDismissExec,
                condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: DISMISS_LOG]") == true
            );

            // ==========================================
            // 4. RUN THE WORKFLOW AUTONOMOUSLY
            // ==========================================
            var workflow = builder.Build();

            // This message is now purely contextual for the agent's reasoning —
            // it is no longer relied upon to supply a filename the tool needs,
            // since DownloadAndParsePdfAsync closes over `name` directly.
            var startMessage = new ChatMessage(ChatRole.User, $"Please process my latest statement file: {name}");

            await using var run = await InProcessExecution.RunStreamingAsync(workflow, startMessage);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (var evt in run.WatchStreamAsync())
            {
                if (evt is ExecutorFailedEvent failed)
                {
                    _logger.LogError($"[NODE CRASHED - {failed.ExecutorId}]: {failed.Data}");
                }
            }

            _logger.LogInformation($"Background Workflow Execution Complete for {name}");
        }
    }
}