using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.SemanticKernel;
using ModelContextProtocol; // ◄ Native SDK namespacing
using ModelContextProtocol.Server;
using OmniGuard.RetrievalEngine.Models;
using OmniGuard.RetrievalEngine.Plugins;
using OmniGuard.RetrievalEngine.Services;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.ComponentModel;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// Check if launched by an IDE Agent client
bool isMcpMode = args.Contains("--mcp");

if (isMcpMode)
{
    // CRITICAL 1: Disable all standard console logging to protect the JSON-RPC channel
    builder.Logging.ClearProviders();

    // CRITICAL 2: Tell ASP.NET Core not to bind to any HTTP ports or boot Kestrel
    builder.WebHost.UseUrls();
}

string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);
string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);

// =========================================================================
// --- NATIVE OPENTELEMETRY TRACING CONFIGURATION FOR LANGFUSE US CLOUD ---
// =========================================================================
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OmniGuard-Local-Engine"))
    .WithTracing(tracing => tracing
        .AddSource("OmniGuard-Local-Engine")
        .AddSource("Microsoft.SemanticKernel*")
        .AddHttpClientInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("https://langfuse.com");
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
            options.Headers = "Authorization=Basic PASTE__KEY_HERE";
        }));
// =========================================================================

// Infrastructure Layer Registrations
builder.Services.AddHttpClient("PineconeClient", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Pinecone:HostUrl"]!);
    client.DefaultRequestHeaders.Add("Api-Key", builder.Configuration["Pinecone:ApiKey"]);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();
    kernelBuilder.AddBertOnnxEmbeddingGenerator(
       onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
       vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
    );
    return kernelBuilder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
});

builder.Services.AddTransient<Kernel>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var kernelBuilder = Kernel.CreateBuilder();
    kernelBuilder.AddOpenAIChatCompletion(
        modelId: modelId,
        apiKey: apiKey,
        endpoint: new Uri("https://huggingface.co"),
        serviceId: "HuggingFaceChat"
    );
    return kernelBuilder.Build();
});

builder.Services.AddScoped<PolicySearchPlugin>();
builder.Services.AddTransient<ComplianceFlowService>();
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.AddBlobServiceClient(builder.Configuration["StorageConnection:blobServiceUri"]!).WithName("StorageConnection");
    clientBuilder.AddQueueServiceClient(builder.Configuration["StorageConnection:queueServiceUri"]!).WithName("StorageConnection");
    clientBuilder.AddTableServiceClient(builder.Configuration["StorageConnection:tableServiceUri"]!).WithName("StorageConnection");
});

// =========================================================================
// --- MODEL CONTEXT PROTOCOL (MCP) SERVICE CONFIGURATION -----------------
// =========================================================================
if (isMcpMode)
{
    // Register the server container and configure the standard IO transport natively
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "OmniGuard-Core-Engine", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(); // Automatically scans for types decorated with [McpServerToolType]
}

var app = builder.Build();

if (isMcpMode)
{
    // Start listening on the clean Stdio channel and block the execution from starting Kestrel
    await app.RunAsync();
    return;
}

// Standard Minimal API Endpoints (Preserved for regular HTTP testing/UI)
app.MapPost("/api/retrieve", async (ComplianceFlowService complianceFlow, ComplianceQueryRequest request) =>
{
    try
    {
        var resultPayload = await complianceFlow.RunComplianceFlowAsync(request.UserPrompt);
        return Results.Ok(resultPayload);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/retrieve1", async (PolicySearchPlugin searchPlugin, ComplianceQueryRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.UserPrompt))
    {
        return Results.BadRequest(new { error = "User compliance prompt cannot be empty." });
    }
    try
    {
        string rawContextResult = await searchPlugin.SearchPolicyKnowledgebase(request.UserPrompt);
        return Results.Ok(new { ProcessedQuery = request.UserPrompt, Timestamp = DateTime.UtcNow, RetrievedContext = rawContextResult });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Direct retrieval engine failure: {ex.Message}");
    }
});

app.Run();

// =========================================================================
// --- DISCOVERABLE MCP TOOL DEFINITIONS MATRIX ---------------------------
// =========================================================================
[McpServerToolType]
public static class OmniGuardMcpTools
{
    [McpServerTool]
    [Description("Runs the complete sequential agent auditor loop on a compliance clause to get verification and scoring.")]
    public static async Task<string> ExecuteComplianceAudit([Description("The exact mortgage rule clause text or user query to verify.")] string userPrompt, ComplianceFlowService complianceFlow)
    {
        // Parameter Injection auto-resolves scoped service directly from the request context
        var resultPayload = await complianceFlow.RunComplianceFlowAsync(userPrompt);
        return $"Status: Passed\nPayload: {resultPayload}";
    }

    [McpServerTool]
    [Description("Exposes raw, factual compliance data extracted straight from the direct metal RRF pipe.")]
    public static async Task<string> FetchRawComplianceFeed(
    [Description("The specific baseline keywords or policy sections to fetch.")] string searchTerms,
    PolicySearchPlugin searchPlugin)
    {
        // Passes the dynamic user input term directly down to your ONNX/Pinecone/SQL pipeline
        string rawContextResult = await searchPlugin.SearchPolicyKnowledgebase(searchTerms);
        return rawContextResult;
    }
}