using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OmniGuard.Compliance.Engine.Agents;
using OmniGuard.Compliance.Engine.Evaluation;
using OmniGuard.Compliance.Engine.Services;
using System;
using NativePineconeClient = Pinecone.PineconeClient;

// Disable all telemetry that causes the DiagnosticsHelper to crash in .NET 9
//Environment.SetEnvironmentVariable("DOTNET_Metrics_Enable_System_Net_Http", "0");
//Environment.SetEnvironmentVariable("DOTNET_Metrics_Enable_System_Net_NameResolution", "0");

// Created embeddign generattor and pas sit as dependency injection
// IKernelBuilderKernelBuilder is used to orchestration so not much to change if I need to move from Onxx to openAi or Azure, hugging face. And here I am using multi llm , local and hugging face using hugging face inference
string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  // Or any Chat-optimized model
string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddBertOnnxEmbeddingGenerator(
    onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
    vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
).AddOpenAIChatCompletion
(
    modelId: modelId,
    apiKey: apiKey,
    endpoint: new Uri("https://router.huggingface.co/v1")
    );
// This stops Semantic Kernel from trying to record metrics that trigger the missing method
Kernel kernel = kernelBuilder.Build();
var embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var chatService = kernel.GetRequiredService<IChatCompletionService>();// Get the chat service

// this builder is applicato builder used in core applications.nothing to with kernel
var builder = Host.CreateApplicationBuilder(args);

// 2. CLEAR the default logging (this removes the problematic EventLog provider)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(); // Only use the Console for now


// 1. Register the Local Embedding Generator
builder.Services.AddSingleton(embeddingGenerator);
builder.Services.AddSingleton(chatService);
builder.Services.AddSingleton(kernel);

// 2. Register Pinecone Client
#pragma warning disable SKEXP0020 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
builder.Services.AddSingleton(sp =>
    new NativePineconeClient(apiKey:
        Environment.GetEnvironmentVariable("Pinecone_APIKey") 

    ));
#pragma warning restore SKEXP0020 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// 3. Register our Custom Services
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<OmniGuardAgents>();
builder.Services.AddSingleton<EvaluationService>();
builder.Services.AddSingleton<HybridIngestionService>();
builder.Services.AddSingleton<HybridRetrievalService>();
builder.Services.AddSingleton<SQLLiteService>();

var host = builder.Build();
Console.WriteLine($"""
    For Vector Ingestion Press 1.
    For Hybrid Ingestion Press 2.
    For Vector base Retrieval Press 3.
    For Hybrid (Vevotr and Keyword) Search Press 4.
    Running Retrieval and Judge using multi agents Press 5
    For Evaluation Press 6.
    """);
var response = Console.ReadLine();
if (response.ToLower().Equals("1"))
{

    // --- RUN THE INDEXER ---
    var ingestion = host.Services.GetRequiredService<IngestionService>();

    Console.WriteLine("Starting Ingestion for first 200 pages...");
    await ingestion.IndexLargePolicyAsync();
    Console.WriteLine(" Ingestion Complete.");
}

if (response.ToLower().Equals("2"))
{

    // --- RUN THE HYRBRID INDEXER ---
    var ingestion = host.Services.GetRequiredService<HybridIngestionService>();

    Console.WriteLine("Starting Ingestion for first 200 pages...");
    await ingestion.GenerateHybridVecors();
    Console.WriteLine(" Hybrid Ingestion Complete.");
}


if (response.ToLower().Equals("3"))
{
    // Run retrieval
    var retrieval = host.Services.GetRequiredService<RetrievalService>();

    Console.WriteLine("\n--- AI Compliance Engine ---");
    Console.Write("Enter your question (e.g. Mortgage Rates): ");
    string? userQuestion = Console.ReadLine();

    if (!string.IsNullOrEmpty(userQuestion))
    {
        Console.WriteLine("Searching authoritative documents...");
        var fullContext = await retrieval.GetComplianceAnswerAsync(userQuestion);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(fullContext);
    }
}

if (response.ToLower().Equals("4"))
{
    // Run retrieval
    var hybridRetrival = host.Services.GetRequiredService<HybridRetrievalService>();

    Console.WriteLine("\n--- AI Compliance Engine ---");
    Console.Write("Enter your question (e.g. Mortgage Rates): ");
    string? userQuestion = Console.ReadLine();

    if (!string.IsNullOrEmpty(userQuestion))
    {
        Console.WriteLine("Searching authoritative documents uisng keyword and vectors...");
        var fullContext = await hybridRetrival.GetFinalResponseAsync(userQuestion);

        Console.WriteLine(fullContext);
    }
}
#region Running Retrieval and Judge using multi agents
if (response.ToLower().Equals("5"))
{
    var agents = host.Services.GetRequiredService<OmniGuardAgents>();
    Console.WriteLine("OmniGuard Multi-Agent System Ready.");
    Console.Write("Query: ");
    var query = Console.ReadLine();

    if (!string.IsNullOrEmpty(query))
    {
        // This starts the "conversation" between Researcher and Auditor
        await agents.RunComplianceFlowAsync(query);
    }
}

#endregion

#region Run Evaluation on Test Data
if (response.ToLower().Equals("6"))
{
    var evaluationService = host.Services.GetRequiredService<EvaluationService>();
    Console.WriteLine("Evaluation is in progress");

    await evaluationService.RunEvaliuationSuiteAsync();
}

#endregion