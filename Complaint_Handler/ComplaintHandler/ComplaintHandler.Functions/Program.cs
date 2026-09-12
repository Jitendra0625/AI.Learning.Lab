using Azure;
using Azure.AI.OpenAI;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource("ComplaintHandler.GenerateLetter")
            .AddSource("ComplaintHandler.RedactComplaint")   
            .AddSource("ComplaintHandler.SaveComplaint")     
        )
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["Cosmos:Endpoint"];
    var key = config["Cosmos:Key"];
    return new CosmosClient(endpoint, key);
});

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["AzureOpenAI:Endpoint"];
    var key = config["AzureOpenAI:Key"];
    return new AzureOpenAIClient(new Uri(endpoint!), new AzureKeyCredential(key!));
});


builder.Build().Run();
