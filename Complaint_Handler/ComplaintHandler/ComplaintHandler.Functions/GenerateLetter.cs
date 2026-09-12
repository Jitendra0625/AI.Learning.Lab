using Azure;
using Azure.AI.ContentSafety;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
//using OpenAI.Chat;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Core;

namespace ComplaintHandler.Functions;

public class GenerateLetter
{
    private readonly ILogger _logger;
    private readonly ContentSafetyClient _client;
    private readonly HttpClient _httpClient;
    private string _key;
    private string _endpoint;
    private readonly AzureOpenAIClient _openAiClient;
    private readonly string _deploymentName;
    // In GenerateLetter.cs, near the top of the class
    private static readonly ActivitySource ActivitySource = new("ComplaintHandler.GenerateLetter");

    public GenerateLetter(ILoggerFactory loggerFactory, AzureOpenAIClient azopenAIClient, IConfiguration config)
    {
        _logger = loggerFactory.CreateLogger<GenerateLetter>();
        var endPoint = Environment.GetEnvironmentVariable("ContentSafety__Endpoint");
        var key = Environment.GetEnvironmentVariable("ContentSafety__Key");
        _key = Environment.GetEnvironmentVariable("ContentSafety__Key");
        _endpoint = Environment.GetEnvironmentVariable("ContentSafety__Endpoint")!.TrimEnd('/');
        _client = new ContentSafetyClient(new Uri(endPoint!), new Azure.AzureKeyCredential(key!));
        _httpClient = new HttpClient();
        _openAiClient = azopenAIClient;
        _deploymentName = config["AzureOpenAI:DeploymentName"]!;
    }
    private static async Task<string> GetAgentServiceTokenAsync()
    {
        TokenCredential credential = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") != null
            ? new ManagedIdentityCredential()   // running in Azure
            : new AzureCliCredential();          // local dev fallback

        var scope = Environment.GetEnvironmentVariable("AgentService__Scope")!;
        var tokenRequestContext = new TokenRequestContext(new[] { scope });

        var token = await credential.GetTokenAsync(tokenRequestContext, default);
        return token.Token;
    }
    [Function("GenerateLetter")]
    public async Task<HttpResponseData> Run(
         [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var request = await req.ReadFromJsonAsync<GenerateLetterRequest>();
        // --- Prompt Shields: check for injection/jailbreak attempts FIRST ---

        var shieldUrl = $"{_endpoint}/contentsafety/text:shieldPrompt?api-version=2024-09-01";
        var shieldPayload = JsonSerializer.Serialize(new
        {
            userPrompt = request.RedactedText,
            documents = new string[] { }
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, shieldUrl)
        {
            Content = new StringContent(shieldPayload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Ocp-Apim-Subscription-Key", _key);

        using var promptShieldsActivity = ActivitySource.StartActivity("prompt_shields.check", ActivityKind.Client);

        HttpResponseMessage shieldHttpResponse;
        try
        {
            shieldHttpResponse = await _httpClient.SendAsync(httpRequest);
            shieldHttpResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Prompt Shields call failed.");
            var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errResponse.WriteStringAsync("Prompt Shields service error.");
            return errResponse;
        }

        var shieldJson = await shieldHttpResponse.Content.ReadAsStringAsync();
        using var shieldDoc = JsonDocument.Parse(shieldJson);
        bool injectionDetected = shieldDoc.RootElement
            .GetProperty("userPromptAnalysis")
            .GetProperty("attackDetected")
            .GetBoolean();

        promptShieldsActivity?.SetTag("prompt_shields.attack_detected", injectionDetected);
        if (injectionDetected)
        {
            promptShieldsActivity?.SetStatus(ActivityStatusCode.Ok, "Blocked: prompt injection detected");
            var blockedResponse = req.CreateResponse(HttpStatusCode.OK);
            await blockedResponse.WriteAsJsonAsync(new GenerateLetterResponse(
                DraftLetter: "",
                WasBlocked: true,
                BlockReason: "Prompt Shields flagged a possible prompt injection or jailbreak attempt.",
                EvaluationNotes: "",
                EvaluationScore: 0
            ));
            return blockedResponse;
        }

        // --- Content Safety pre-check on the incoming (redacted) complaint text ---
        var analysisRequest = new AnalyzeTextOptions(request.RedactedText);
        Response<AnalyzeTextResult> safetyResult;

        using var preCheckActivity = ActivitySource.StartActivity("content_safety.precheck", ActivityKind.Client);

        try
        {
            safetyResult = await _client.AnalyzeTextAsync(analysisRequest);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Content Safety pre-check call failed.");
            var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errResponse.WriteStringAsync("Content Safety service error.");
            return errResponse;
        }

        const int severityThreshold = 2; // flag anything above "safe" — tune this later
        var flaggedCategories = safetyResult.Value.CategoriesAnalysis
            .Where(c => c.Severity > severityThreshold)
            .Select(c => $"{c.Category} (severity {c.Severity})")
            .ToList();
        preCheckActivity?.SetTag("content_safety.max_severity", severityThreshold);
        preCheckActivity?.SetTag("content_safety.stage", "pre");

        if (flaggedCategories.Any())
        {
            preCheckActivity?.SetTag("content_safety.was_blocked", true);
            preCheckActivity?.SetStatus(ActivityStatusCode.Ok, "Blocked: harm category threshold exceeded");

            var blockedResponse = req.CreateResponse(HttpStatusCode.OK);
            await blockedResponse.WriteAsJsonAsync(new GenerateLetterResponse(
                DraftLetter: "",
                WasBlocked: true,
                BlockReason: $"Content Safety pre-check flagged: {string.Join(", ", flaggedCategories)}",
                EvaluationNotes: "",
                EvaluationScore: 0
            ));
            return blockedResponse;
        }
        preCheckActivity?.SetTag("content_safety.was_blocked", false);

        // ---------- 3. Agentic generate + evaluate — NOW A REMOTE CALL ----------
        // Replaces the old in-process AgentWorkflowBuilder.BuildSequential call.
        // The workflow itself (LetterAgent -> EvaluatorAgent) now lives in the
        // standalone ComplaintHandler.AgentService project, called over HTTP.
        //
        // NOTE (deferred to Session 8, per project decision): this call is
        // unauthenticated for now, matching the Functions' current
        // AuthorizationLevel.Anonymous local-dev posture. Managed Identity +
        // Easy Auth will be added once the agent service is hosted on Azure
        // App Service, alongside APIM.
        string draftLetter;
        int evaluationScore;
        string evaluationNotes;

        using (var agentCallActivity = ActivitySource.StartActivity("agent_service.generate_and_evaluate", ActivityKind.Client))
        {
            var agentServiceEndpoint = Environment.GetEnvironmentVariable("AgentService__Endpoint")!;
            agentCallActivity?.SetTag("agent_service.url", agentServiceEndpoint);

            try
            {
                var token = await GetAgentServiceTokenAsync();

                var agentRequestPayload = new
                {
                    ComplaintId = request.ComplaintId,
                    RedactedText = request.RedactedText
                };

                using var agentHttpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{agentServiceEndpoint}/api/generate-and-evaluate")
                {
                    Content = JsonContent.Create(agentRequestPayload)
                };
                agentHttpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                using var agentHttpResponse = await _httpClient.SendAsync(agentHttpRequest);

                if (!agentHttpResponse.IsSuccessStatusCode)
                {
                    var errBody = await agentHttpResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Agent service call failed: {Status} {Body}", agentHttpResponse.StatusCode, errBody);

                    agentCallActivity?.SetStatus(ActivityStatusCode.Error, $"Agent service returned {agentHttpResponse.StatusCode}");

                    var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
                    await errResponse.WriteAsJsonAsync(new { Error = "Agent service error." });
                    return errResponse;
                }

                var agentResult = await agentHttpResponse.Content.ReadFromJsonAsync<AgentServiceResult>()
                    ?? throw new InvalidOperationException("Agent service returned an empty response.");

                draftLetter = agentResult.DraftLetter;
                evaluationScore = agentResult.EvaluationScore;
                evaluationNotes = agentResult.EvaluationNotes;

                agentCallActivity?.SetTag("agent.evaluation_score", evaluationScore);
            }
            catch (Exception ex)
            {
                agentCallActivity?.SetTag("error.type", ex.GetType().Name);
                agentCallActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
                await errResponse.WriteAsJsonAsync(new { Error = "Agent service call failed." });
                return errResponse;
            }
        }

        // --- Content Safety post-check on the generated letter (stays here, at the gateway) ---
        var postCheckRequest = new AnalyzeTextOptions(draftLetter);
        Response<AnalyzeTextResult> postCheckResult;
        try
        {
            postCheckResult = await _client.AnalyzeTextAsync(postCheckRequest);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Content Safety post-check call failed.");
            var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errResponse.WriteAsJsonAsync(new { Error = "Content Safety service error." });
            return errResponse;
        }

        var postCheckFlagged = postCheckResult.Value.CategoriesAnalysis
            .Where(c => c.Severity > severityThreshold)
            .Select(c => $"{c.Category} (severity {c.Severity})")
            .ToList();

        if (postCheckFlagged.Any())
        {
            var blockedResponse = req.CreateResponse(HttpStatusCode.OK);
            await blockedResponse.WriteAsJsonAsync(new GenerateLetterResponse(
                DraftLetter: "",
                WasBlocked: true,
                BlockReason: $"Content Safety post-check flagged the generated letter: {string.Join(", ", postCheckFlagged)}",
                EvaluationNotes: "",
                EvaluationScore: 0
            ));
            return blockedResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new GenerateLetterResponse(
            DraftLetter: draftLetter,
            WasBlocked: false,
            BlockReason: "",
            EvaluationScore: evaluationScore,
            EvaluationNotes: evaluationNotes
        ));
        return response;
    }
}


public record GenerateLetterRequest(string ComplaintId, string RedactedText);
public record GenerateLetterResponse(
    string DraftLetter,
    bool WasBlocked,
    string? BlockReason,
    int? EvaluationScore,
    string? EvaluationNotes
);

// Shape returned by ComplaintHandler.AgentService's /api/generate-and-evaluate endpoint
public record AgentServiceResult(string DraftLetter, int EvaluationScore, string EvaluationNotes);

