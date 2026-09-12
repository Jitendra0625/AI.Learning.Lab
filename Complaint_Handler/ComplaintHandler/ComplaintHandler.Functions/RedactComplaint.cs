using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;

namespace ComplaintHandler.Functions;

public class RedactComplaint
{
    private readonly ILogger<RedactComplaint> _logger;
    private readonly TextAnalyticsClient _client;
    private static readonly ActivitySource ActivitySource = new("ComplaintHandler.RedactComplaint");

    public RedactComplaint(ILogger<RedactComplaint> logger)
    {
        _logger = logger;
        var endPoint = Environment.GetEnvironmentVariable("Language__Endpoint");
        var key = Environment.GetEnvironmentVariable("Language__Key");
        _client = new TextAnalyticsClient(new Uri(endPoint!), new AzureKeyCredential(key!));
    }

    [Function("RedactComplaint")]
    public async Task<HttpResponseData> Run(
       [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<RedactRequest>();
        string originalText = request.ComplaintText;
        using var piiActivity = ActivitySource.StartActivity("language.pii_detection", ActivityKind.Client);

        Response<PiiEntityCollection> piientity= _client.RecognizePiiEntities(originalText);

        // open telementy activity to go in Applicatin insight
        piiActivity?.SetTag("pii.entities_found", piientity.Value.Count);
        piiActivity?.SetTag("pii.categories", string.Join(",", piientity.Value.Select(e => e.Category.ToString()).Distinct()));
        PiiEntityCollection entities = piientity.Value;

        // Offsets are against the ORIGINAL string, so redact rightmost-first —
        // otherwise earlier replacements shift the offsets of everything after them.
        var orderedEntities = entities.OrderByDescending(e => e.Offset).ToList();

        var mappingTable = new Dictionary<string, string>();
        string redactedText = originalText;

        // Track a per-category counter so labels read <person_0>, <person_1>, <address_0>...
        var categoryCounters = new Dictionary<string, int>();

        // Build placeholders in original (left-to-right) order for readable numbering,
        // but apply the actual string edits in descending offset order.
        var ascendingForNumbering = entities.OrderBy(e => e.Offset).ToList();
        var placeholderByEntity = new Dictionary<PiiEntity, string>();

        foreach (var entity in ascendingForNumbering)
        {
            string category = entity.Category.ToString().ToLowerInvariant();
            int count = categoryCounters.GetValueOrDefault(category, 0);
            string placeholder = $"<{category}_{count}>";
            categoryCounters[category] = count + 1;

            placeholderByEntity[entity] = placeholder;
            mappingTable[placeholder] = entity.Text;
        }

        foreach (var entity in orderedEntities)
        {
            string placeholder = placeholderByEntity[entity];
            redactedText = redactedText.Remove(entity.Offset, entity.Length)
                                        .Insert(entity.Offset, placeholder);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new RedactResponse(
            RedactedText: redactedText,
            MappingTable: mappingTable
        ));
        return response;
    }
}

public record RedactRequest(string ComplaintId, string ComplaintText);
public record RedactResponse(string RedactedText, Dictionary<string, string> MappingTable);

