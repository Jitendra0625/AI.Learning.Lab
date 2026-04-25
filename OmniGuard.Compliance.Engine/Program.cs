using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OmniGuard.Compliance.Engine.Models;
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
        Environment.GetEnvironmentVariable("Pinecone_APIKey") // as uisng old package so hase to give environment name

    ));
#pragma warning restore SKEXP0020 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// 3. Register our Custom Services
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<OmniGuardAgents>();

var host = builder.Build();
Console.WriteLine("Ingestion already done, then say no to run the retrieval or yes to to ingestion and then direct retireval say retrieve or for multiagent to retriev say team");
var response = Console.ReadLine();
if (response.ToLower().Equals("yes"))
{

    // --- RUN THE INDEXER ---
    var ingestion = host.Services.GetRequiredService<IngestionService>();

    Console.WriteLine("Starting Ingestion for first 5 pages...");
    await ingestion.IndexLargePolicyAsync();
    Console.WriteLine(" Ingestion Complete.");
}


if (response.ToLower().Equals("retrieve"))
{
    // Run retrieval
    var retrieval = host.Services.GetRequiredService<RetrievalService>();

    Console.WriteLine("\n--- AI Compliance Engine ---");
    Console.Write("Enter your question (e.g. Mortgage Rates): ");
    string? userQuestion = Console.ReadLine();

    if (!string.IsNullOrEmpty(userQuestion))
    {
        Console.WriteLine("Searching authoritative documents...");
        var fullContext = await retrieval.GetFinalResponseAsync(userQuestion);

        Console.WriteLine(fullContext);
    }
}
#region Running Retrieval and Judge using multi agents
if (response.ToLower().Equals("team"))
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