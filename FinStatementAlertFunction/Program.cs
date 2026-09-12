using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
//using Azure.Identity;
using Azure.AI.OpenAI;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Azure.Monitor.OpenTelemetry.Exporter;
using System;
using Azure.Identity;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // 1. Load Endpoints (No Keys!)
        var openAiEndpoint = Environment.GetEnvironmentVariable("FoundryEndpoint")!;
        var docIntelEndpoint = Environment.GetEnvironmentVariable("DocIntelEndpoint")!;
        var storageAccountName = Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName")!;
        var appInsightsConnString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")!;

        // 2. Initialize the Managed Identity Credential
        var credential = new DefaultAzureCredential();

        // 3. Register Azure AI Foundry Client
        var openAiClient = new AzureOpenAIClient(new Uri(openAiEndpoint), credential);
        IChatClient chatClient = openAiClient.GetChatClient("chatmodel")
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: "Experimental.Microsoft.Extensions.AI")
            .Build();

        services.AddSingleton(chatClient);

        // 4. Register Blob Client using the container URL
        var containerUri = new Uri($"https://{storageAccountName}.blob.core.windows.net/bank-statements");
        services.AddSingleton(new BlobContainerClient(containerUri, credential));

        // 5. Register Document Intelligence Client
        services.AddSingleton(new DocumentIntelligenceClient(new Uri(docIntelEndpoint), credential));

        // 6. Register OpenTelemetry
        Sdk.CreateTracerProviderBuilder()
            .AddSource("Experimental.Microsoft.Extensions.AI", "Microsoft.Agents.AI")
            .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnString)
            .Build();

        Sdk.CreateMeterProviderBuilder()
            .AddMeter("Experimental.Microsoft.Extensions.AI", "Microsoft.Agents.AI")
            .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnString)
            .Build();
    })
    .Build();

host.Run();