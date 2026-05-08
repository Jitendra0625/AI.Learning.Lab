using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using OmniGuard.Compliance.Engine.Services;
using NativePineconeClient = Pinecone.PineconeClient;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // 1. Register dependencies
        services.AddSingleton<SQLLiteService>();

        // 2. Register Pinecone using the Env Var
        //services.AddSingleton(new NativePineconeClient(
        //    Environment.GetEnvironmentVariable("Pinecone_APIKey")));

        // 2. Register Pinecone using the Env Var
        //services.AddSingleton(new NativePineconeClient();
        services.AddSingleton<NativePineconeClient>(sp =>
        {
            // This code only runs when the Function actually starts processing
            return new NativePineconeClient("pcsk_5cmXmJ_QiniQ3YYsxjz5mR5bAGi6R4XiidzKeZUiw6ULujhQxtm1L29VaxCdeHLEZPsZeV");
        });

        // 3. Register the Embedding Generator as a LAZY factory
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("DEBUG: Starting ONNX model load...");

            var builder = Kernel.CreateBuilder();
            builder.AddBertOnnxEmbeddingGenerator(
               onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
               vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
            );

            var result = builder.Build().GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

            stopwatch.Stop();
            Console.WriteLine($"DEBUG: ONNX model loaded in {stopwatch.ElapsedMilliseconds}ms");
            return result;
        });
        // 4. Register the main service
         services.AddSingleton<HybridIngestionService>();
    })
    .Build();

await host.RunAsync();