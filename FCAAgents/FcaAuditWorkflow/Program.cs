using Azure;
using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI;
using System.ClientModel;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Azure.Monitor.OpenTelemetry.Exporter;
using System.ComponentModel;

// ==========================================
// 1. INITIALIZE CLIENTS
// ==========================================

// Connect to Azure AI Foundry (Your LLM brain)
var openAiEndpoint = new Uri("foundry openai url");
var openAiKey = new ApiKeyCredential("foundry key");

var openAiClient = new AzureOpenAIClient(openAiEndpoint, openAiKey);
var chatClient = openAiClient.GetChatClient("chatmodel").AsIChatClient();

// Wrap the client with the OpenTelemetry middleware
IChatClient chatClient1 = chatClient
    .AsBuilder()
    .UseOpenTelemetry(
        sourceName: "Experimental.Microsoft.Extensions.AI",
        configure: options => options.EnableSensitiveData = true // Optional: Set to true if you want to capture the actual prompt/response text in your traces
    )
    .Build();
// Connect to Azure AI Search (Your Knowledge Base)
var searchKey = new AzureKeyCredential("ai search key");
var searchEndpoint = new Uri("https://<AISearch>.search.windows.net");
var searchClient = new SearchClient(searchEndpoint, "indexname", searchKey);

// ==========================================
// 2. DEFINE THE RAG TOOL
// ==========================================

[Description("Searches the FCA regulatory knowledge base for exact clauses and rules.")]
async Task<string> SearchFcaRulesAsync([Description("The exact search query keywords")] string query)
{
    Console.WriteLine($"\n[System]: Triggering Hybrid Search (RRF) for '{query}'...");

    var options = new SearchOptions
    {
        Size = 3,
        VectorSearch = new()
        {
            Queries = {
                new VectorizableTextQuery(query)
                {
                    KNearestNeighborsCount = 3,
                    Fields = { "vector" } // Your exact vector field name
                }
            }
        }
    };

    var response = await searchClient.SearchAsync<SearchDocument>(query, options);

    var results = response.Value.GetResults();
    var retrievedText = string.Join("\n\n", results.Select(r => r.Document["chunk"].ToString())); // Your exact text field name

    return string.IsNullOrWhiteSpace(retrievedText) ? "No matching rules found." : retrievedText;
}

var fcaSearchTool = AIFunctionFactory.Create(SearchFcaRulesAsync);

// ==========================================
// 3. CREATE THE AGENTS
// ==========================================

    var routerAgent = chatClient1.AsAIAgent(
        name: "RouterAgent",
        instructions: "Classify user intent as RETRIEVAL_REQUIRED or DIRECT_RESPONSE."
    );

var fcaAgent = chatClient1.AsAIAgent(
    name: "MyFCAAgent",
    instructions: "You are the FCA regulatory search specialist. Use your search tool to query the connected knowledge base and retrieve exact regulatory clauses.",
    tools: [fcaSearchTool] // Giving the agent access to the search tool
);

var auditAgent = chatClient1.AsAIAgent(
    name: "AuditAgent",
    instructions: "Audit the retrieved information for regulatory compliance. Conclude your final response explicitly with 'Orchestrated Verdict: AUDIT PASSED' or 'Orchestrated Verdict: AUDIT FAILED'."
);

// ==========================================
// 4. BUILD THE WORKFLOW GRAPH
// ==========================================

// FIX 1: Agents output message lists, not raw strings. We must cast the Action to accept IReadOnlyList<ChatMessage>
var notifyUserExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
    Console.WriteLine($"\n✅ [Response to User]: {messages.LastOrDefault()?.Text}")).BindAsExecutor("NotifyUser");

var humanEscalationExec = new Action<IReadOnlyList<ChatMessage>>(messages =>
    Console.WriteLine($"\n🚨 [ALERT - Human Intervention Required]: {messages.LastOrDefault()?.Text}")).BindAsExecutor("HumanEscalation");

var builder = new WorkflowBuilder(routerAgent);

builder.AddEdge(routerAgent, fcaAgent);
builder.AddEdge(fcaAgent, auditAgent);

// FIX 2: Check the text of the LAST message in the list for the Audit conditions
builder.AddEdge<IReadOnlyList<ChatMessage>>(
    source: auditAgent,
    target: notifyUserExec,
    condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("Orchestrated Verdict: AUDIT PASSED") == true
);

builder.AddEdge<IReadOnlyList<ChatMessage>>(
    source: auditAgent,
    target: humanEscalationExec,
    condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("Orchestrated Verdict: AUDIT PASSED") == false
);

var workflow = builder.Build();

// ==========================================
// 4.5. SETUP OPENTELEMETRY & APP INSIGHTS
// ==========================================
var appInsightsConnectionString = "InstrumentationKey=MyKey;IngestionEndpoint=my_endpoint/;";

// Configure Tracing (Tracks request flows, latency, and agent routing)
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("Experimental.Microsoft.Extensions.AI") // Catches LLM calls and tool invocations
    .AddSource("Microsoft.Agents.AI")                  // Catches agent framework routing
    .AddAzureMonitorTraceExporter(options => options.ConnectionString = appInsightsConnectionString)
    .Build();

// Configure Metrics (Tracks token usage, costs, and aggregations)
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("Experimental.Microsoft.Extensions.AI")
    .AddMeter("Microsoft.Agents.AI")
    .AddAzureMonitorMetricExporter(options => options.ConnectionString = appInsightsConnectionString)
    .Build();
// ==========================================
// 5. EXECUTION & BOOT SEQUENCE
// ==========================================

Console.WriteLine("\n=============================================");
Console.WriteLine("Starting Multi-Agent Workflow Execution");
Console.WriteLine("=============================================\n");

var userText = "What does FCA rule MCOB 2.6.2 say about customer liabilities? Please check the exact clause.";
// FIX 3: Pass a single ChatMessage, not a List container!
var startMessage = new ChatMessage(ChatRole.User, userText);

Console.WriteLine($"[User Input]: {userText}\n");

await using var run = await InProcessExecution.RunStreamingAsync(workflow, startMessage);

// Emit the token to wake up the RouterAgent
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

// FIX 4: The Ultimate Diagnostic Loop. Catch EVERYTHING.
await foreach (var evt in run.WatchStreamAsync())
{
    if (evt is AgentResponseUpdateEvent update)
    {
        Console.Write(update.Update.Text);
    }
    else if (evt is ExecutorInvokedEvent invoked)
    {
        Console.WriteLine($"\n\n [Starting Agent]: {invoked.ExecutorId}...");
    }
    else if (evt is ExecutorCompletedEvent completed)
    {
        Console.WriteLine($"\n [Finished Agent]: {completed.ExecutorId}");
    }
    else if (evt is ExecutorFailedEvent failed)
    {
        // If an agent crashes, it will print in red here!
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[AGENT CRASHED - {failed.ExecutorId}]: {failed.Data}");
        Console.ResetColor();
    }
    else if (evt is WorkflowErrorEvent wfError)
    {
        // If the workflow itself fails, it will print here!
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[WORKFLOW CRASHED]: {wfError.Exception.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("\n\n=============================================");
Console.WriteLine("🏁 Workflow Execution Complete");
Console.WriteLine("=============================================\n");