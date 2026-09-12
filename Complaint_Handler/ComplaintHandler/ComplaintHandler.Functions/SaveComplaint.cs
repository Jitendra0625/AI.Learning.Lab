using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net;

namespace ComplaintHandler.Functions;

public class SaveComplaint
{
    private readonly ILogger _logger;
    private readonly CosmosClient _cosmosClient;
    private const string DatabaseName = "complainthandlerdb";
    private const string ContainerName = "complaints";
    private static readonly ActivitySource ActivitySource = new("ComplaintHandler.SaveComplaint");
    public SaveComplaint(ILoggerFactory loggerFactory, CosmosClient client)
    {
        _logger = loggerFactory.CreateLogger<SaveComplaint>();
        _cosmosClient = client;
    }

    [Function("SaveComplaint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<SaveComplaintRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.ComplaintId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { Error = "ComplaintId and body are required." });
            return bad;
        }
        using var cosmosActivity = ActivitySource.StartActivity("cosmosdb.create_item", ActivityKind.Client);

        cosmosActivity?.SetTag("db.system", "cosmosdb");
        cosmosActivity?.SetTag("db.name", DatabaseName);
        cosmosActivity?.SetTag("db.cosmosdb.container", ContainerName);

        var document = new ComplaintDocument(
           request.ComplaintId,
           request.FinalLetter,
           request.Rating,
           request.HandlerFeedback,
           request.RelevanceScore,
           DateTime.UtcNow);

        var container = _cosmosClient.GetContainer(DatabaseName, ContainerName);
        try
        {
            var response1 = await container.CreateItemAsync(document, new PartitionKey(document.Id));
            cosmosActivity?.SetTag("db.cosmosdb.request_charge", response1.RequestCharge);
            cosmosActivity?.SetTag("db.cosmosdb.status_code", (int)response1.StatusCode);
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos write failed for complaint {ComplaintId}", request.ComplaintId);
            cosmosActivity?.SetTag("error.type", ex.GetType().Name);
            cosmosActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var err = req.CreateResponse((HttpStatusCode)ex.StatusCode);
            await err.WriteAsJsonAsync(new { Error = ex.Message });
            return err;
        }

        _logger.LogInformation("Saved complaint {ComplaintId} with rating {Rating}", request.ComplaintId, request.Rating);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { Status = "Saved", request.ComplaintId });
        return response;
    }
}

public record SaveComplaintRequest(
    string ComplaintId,
    string FinalLetter,
    int Rating,
    string? HandlerFeedback, double? RelevanceScore);

public record ComplaintDocument(
    [property: JsonProperty("id")] string Id,
    string FinalLetter,
    int Rating,
    string? HandlerFeedback,
    double? RelevanceScore,
    DateTime SavedAtUtc);