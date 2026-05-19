using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Omniguard.Ingestion;
using OmniGuard.Compliance.Engine.Services;
using System.Net.Http.Headers;
using Ingestion = Omniguard.Ingestion;
using NativePineconeClient = Pinecone.PineconeClient;
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // 1. Register dependencies
        services.AddSingleton<Ingestion.SQLLiteService>();

        // Register Pinecone using the Env Var
        //services.AddSingleton(new NativePineconeClient(
        //    Environment.GetEnvironmentVariable("Pinecone_APIKey")));

        // 2. Register Pinecone using the Env Var
        //services.AddSingleton(new NativePineconeClient();
        services.AddSingleton<NativePineconeClient>(sp =>
        {
            // This code only runs when the Function actually starts processing
            return new NativePineconeClient("pcsk_2wiP4A_QGqMaTgwd65GnLA2bvi8U2EcDdFuTd3Zd8FEGmbqyPS97R6EbkiX1EvsrJ3M1gJ");
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

        services.AddSingleton<Ingestion.HybridIngestionsService>();



        // 5. Register your Entity Framework Core SQL Server Context
        string? connectionString = context.Configuration["SqlConnectionString"];
        services.AddDbContext<AuditDbContext>(options =>
            options.UseSqlServer(connectionString));

        //6. Registr Audit Service
        services.AddTransient<AuditService>();

        // 5. Register your Entity Framework Core SQL Server Context
        string? connectionStringIngestion = context.Configuration["SqlConnectionStringIngestion"];
        services.AddSingleton<IStagingBufferRepository>(new StagingBufferRepository(connectionStringIngestion));

        // 8. Provision Named HttpClient connection pool for Pinecone REST API Operations
        string? pineconeApiKey =  Environment.GetEnvironmentVariable("Pinecone_APIKey")
            ?? throw new InvalidOperationException("PineconeApiKey configuration missing.");
        string? pineconeUrl = Environment.GetEnvironmentVariable("PineconeIndexUrl")
            ?? throw new InvalidOperationException("PineconeIndexUrl configuration missing.");

        services.AddHttpClient("PineconeBatchClient", client =>
        {
            client.BaseAddress = new Uri(pineconeUrl);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("Api-Key", pineconeApiKey);
        });
        services.AddSingleton<IStagingBatchRepository>(
    new StagingBatchRepository(connectionStringIngestion));
    })
    .Build();

await host.RunAsync();