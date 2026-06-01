using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OmniGuard.RetrievalEngine.Models;
using OmniGuard.RetrievalEngine.Plugins;
using OmniGuard.RetrievalEngine.Services;
using Microsoft.Extensions.Azure;
using OpenTelemetry.Trace; // ◄ Added for tracing infrastructure
using OpenTelemetry.Resources; // ◄ Added for service definitions

var builder = WebApplication.CreateBuilder(args);
string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  
string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);

// =========================================================================
// --- NATIVE OPENTELEMETRY TRACING CONFIGURATION FOR LANGFUSE US CLOUD ---
// =========================================================================
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OmniGuard-Local-Engine"))
    .WithTracing(tracing => tracing
        .AddSource("OmniGuard-Local-Engine")     // Captures your ComplianceFlowService custom tags
        .AddSource("Microsoft.SemanticKernel*")   // Automatically monitors SK internal orchestration spans
        .AddHttpClientInstrumentation()           // Tracks latency variations to Pinecone and HuggingFace endpoints
        .AddAspNetCoreInstrumentation()           // Automatically maps incoming Minimal API request attributes
        .AddOtlpExporter(options =>
        {
            // Pointing to your dedicated US Cloud OTLP Intake Router
            //options.Endpoint = new Uri("https://us.otel.langfuse.com");
            options.Endpoint = new Uri("https://us.cloud.langfuse.com/api/public/otel/v1/traces");
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;

            // Inject the secure basic auth Base64 credentials token generated with Bash
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

// Register Semantic Kernel with Hugging Face Chat Completion service configuration
builder.Services.AddTransient<Kernel>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var kernelBuilder = Kernel.CreateBuilder();

    kernelBuilder.AddOpenAIChatCompletion(
        modelId: modelId,
        apiKey: apiKey,
        endpoint: new Uri("https://router.huggingface.co/v1"),
        serviceId: "HuggingFaceChat"
    );

    return kernelBuilder.Build();
});

// Register the custom search plugin and our agent flow service
builder.Services.AddScoped<PolicySearchPlugin>();
builder.Services.AddTransient<ComplianceFlowService>();
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.AddBlobServiceClient(builder.Configuration["StorageConnection:blobServiceUri"]!).WithName("StorageConnection");
    clientBuilder.AddQueueServiceClient(builder.Configuration["StorageConnection:queueServiceUri"]!).WithName("StorageConnection");
    clientBuilder.AddTableServiceClient(builder.Configuration["StorageConnection:tableServiceUri"]!).WithName("StorageConnection");
});

var app = builder.Build();

// Expose the clean Agentic compliance execution flow to HTTP POST queries
app.MapPost("/api/retrieve", async (ComplianceFlowService complianceFlow, ComplianceQueryRequest request) =>
{
    try
    {
        // Executes the turn-by-turn Agent Framework chain process cleanly
        var resultPayload = await complianceFlow.RunComplianceFlowAsync(request.UserPrompt);

        // Returns the final results payload back out to the web client
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
        // Bypasses the ComplianceFlowService agent chain entirely 
        // Directly executes the 384-dim ONNX + Pinecone + SQL BM25 + Azurite pipeline
        string rawContextResult = await searchPlugin.SearchPolicyKnowledgebase(request.UserPrompt);

        // Returns the final blended text payload back out to the web client
        return Results.Ok(new
        {
            ProcessedQuery = request.UserPrompt,
            Timestamp = DateTime.UtcNow,
            RetrievedContext = rawContextResult
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Direct retrieval engine failure: {ex.Message}");
    }
});

app.Run();