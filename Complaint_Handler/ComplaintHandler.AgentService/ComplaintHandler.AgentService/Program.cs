using System.Diagnostics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ComplaintHandler.AgentService;

// Alias needed because both Microsoft.Extensions.AI.ChatMessage and
// OpenAI.Chat.ChatMessage are in scope transitively — same ambiguity hit in
// the original in-process version (Session 6/7).
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Config: same values GenerateLetter's local.settings.json already has ---
var openAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Missing config: AzureOpenAI:Endpoint");
var openAiKey = builder.Configuration["AzureOpenAI:Key"]
    ?? throw new InvalidOperationException("Missing config: AzureOpenAI:Key");
var deploymentName = builder.Configuration["AzureOpenAI:DeploymentName"]
    ?? throw new InvalidOperationException("Missing config: AzureOpenAI:DeploymentName");

// --- OpenTelemetry: same pattern as the Functions app (Session 6), so traces
// from this service line up with the rest of the pipeline in Application Insights ---
var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
const string ActivitySourceName = "ComplaintHandler.AgentService";
var activitySource = new ActivitySource(ActivitySourceName);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource(ActivitySourceName);
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
    })
    .UseAzureMonitor(o =>
    {
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            o.ConnectionString = appInsightsConnectionString;
        }
    });

var app = builder.Build();

// NOTE (deferred to Session 8, per project decision): this endpoint is
// intentionally unauthenticated for now, matching the Functions' current
// AuthorizationLevel.Anonymous local-dev posture. Managed Identity + Easy Auth
// will be added when this service moves to Azure App Service in Session 8,
// alongside APIM. Do not deploy this service publicly before that's done.
app.MapPost("/api/generate-and-evaluate", async (GenerateAndEvaluateRequest request) =>
{
    var openAiClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new Azure.AzureKeyCredential(openAiKey));
    var chatClient = openAiClient.GetChatClient(deploymentName);

    using var agentActivity = activitySource.StartActivity("agent.generate_and_evaluate", ActivityKind.Server);
    agentActivity?.SetTag("gen_ai.system", "az.ai.openai");
    agentActivity?.SetTag("gen_ai.request.model", deploymentName);
    agentActivity?.SetTag("gen_ai.operation.name", "chat");

    try
    {
        IChatClient iChatClient = chatClient.AsIChatClient();

        AIAgent letterAgent = iChatClient.AsAIAgent(
            instructions:
                "You are a customer service assistant drafting a professional, empathetic response " +
                "letter to a customer complaint. The complaint text has had personal information " +
                "redacted (shown as placeholders like <person_0>) — do not attempt to fill these in " +
                "or refer to the redaction. Write a concise, courteous letter acknowledging the issue " +
                "and outlining next steps. Do not invent specific compensation amounts or promises " +
                "not implied by the complaint.",
            name: "LetterAgent");

        AIAgent evaluatorAgent = iChatClient.AsAIAgent(
            instructions:
                "You evaluate a draft complaint-response letter against the original complaint. " +
                "Score 1-5 on: tone/empathy, relevance to the complaint, and whether it invents " +
                "any promises or facts not present in the complaint. " +
                "Respond ONLY with JSON: { \"score\": int, \"notes\": string }.",
            name: "EvaluatorAgent");

        var workflow = AgentWorkflowBuilder.BuildSequential(letterAgent, evaluatorAgent);

        Run run = await InProcessExecution.RunAsync(workflow, request.RedactedText);

        List<ChatMessage>? outputMessages = null;
        foreach (WorkflowEvent evt in run.NewEvents)
        {
            if (evt is WorkflowOutputEvent outputEvt)
            {
                outputMessages = outputEvt.As<List<ChatMessage>>();
            }
        }

        if (outputMessages is null)
        {
            throw new InvalidOperationException("Sequential workflow did not produce an output event.");
        }

        string draftLetter = outputMessages
            .LastOrDefault(m => m.AuthorName == "LetterAgent")?.Text
            ?? throw new InvalidOperationException("LetterAgent produced no response.");

        string evaluationJson = outputMessages
            .LastOrDefault(m => m.AuthorName == "EvaluatorAgent")?.Text
            ?? throw new InvalidOperationException("EvaluatorAgent produced no response.");
        var caseInsensitiveJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var evaluation = JsonSerializer.Deserialize<LetterEvaluationResult>(evaluationJson, caseInsensitiveJsonOptions)
                    ?? new LetterEvaluationResult(0, "Evaluator response could not be parsed.");

        agentActivity?.SetTag("agent.evaluation_score", evaluation.Score);

        // KNOWN GAP (carried over from Session 6/7, unchanged): token usage
        // (gen_ai.usage.input_tokens / output_tokens) is not yet wired up —
        // the old direct ChatCompletion.Usage property doesn't have an obvious
        // equivalent on this workflow's response object yet. Still to be located.

        return Results.Ok(new GenerateAndEvaluateResponse(draftLetter, evaluation.Score, evaluation.Notes));
    }
    catch (Exception ex)
    {
        agentActivity?.SetTag("error.type", ex.GetType().Name);
        agentActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        return Results.Problem(title: "Agent workflow error.", detail: ex.Message, statusCode: 502);
    }
});

app.Run();
