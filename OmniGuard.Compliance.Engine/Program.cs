using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.AI;
using OmniGuard.Compliance.Engine.Services;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;
using Pinecone = Microsoft.SemanticKernel.Connectors.Pinecone;
using NativePineconeClient = Pinecone.PineconeClient;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Connectors.Onnx;

// Disable all telemetry that causes the DiagnosticsHelper to crash in .NET 9
Environment.SetEnvironmentVariable("DOTNET_Metrics_Enable_System_Net_Http", "0");
Environment.SetEnvironmentVariable("DOTNET_Metrics_Enable_System_Net_NameResolution", "0");

// Created embeddign generattor and pas sit as dependency injection
// IKernelBuilderKernelBuilder is used to orchestration so not much to change if I need to move from Onxx to openAi or Azure, hugging face
IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddBertOnnxEmbeddingGenerator(
    onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
    vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
);
                                 // This stops Semantic Kernel from trying to record metrics that trigger the missing method
Kernel kernel = kernelBuilder.Build();
var embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string,Embedding<float>>>() ;

// this builder is applicato builder used in core applications.nothing to with kernel
var builder = Host.CreateApplicationBuilder(args);

// 2. CLEAR the default logging (this removes the problematic EventLog provider)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(); // Only use the Console for now


// 1. Register the Local Embedding Generator
builder.Services.AddSingleton(embeddingGenerator);

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

var host = builder.Build();
Console.WriteLine("Ingestion already done, then say yes else say no to run the retrieval");
var response=Console.ReadLine();
if (response.ToLower().Equals("yes"))
{

    // --- RUN THE INDEXER ---
    var ingestion = host.Services.GetRequiredService<IngestionService>();

    Console.WriteLine("🚀 Starting Ingestion for first 5 pages...");
    await ingestion.IndexLargePolicyAsync();
    Console.WriteLine("🏁 Ingestion Complete.");
}


// Run retrieval
var retrieval = host.Services.GetRequiredService<RetrievalService>();

Console.WriteLine("\n--- 🤖 AI Compliance Engine ---");
Console.Write("Enter your question (e.g. Mortgage Rates): ");
string? userQuestion = Console.ReadLine();

if (!string.IsNullOrEmpty(userQuestion))
{
    Console.WriteLine("🔍 Searching authoritative documents...");
    var fullContext = await retrieval.GetComplianceAnswerAsync(userQuestion);

    Console.WriteLine(fullContext);
}