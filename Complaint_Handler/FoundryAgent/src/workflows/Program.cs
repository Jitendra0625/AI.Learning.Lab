// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using Azure.Core;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Workflows;

Env.NoClobber().TraversePath().Load();
TokenCredential credential = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") != null 
    || Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") != null
    ? new ManagedIdentityCredential()
    : new AzureCliCredential();

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));
var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-4o";

var projectClient = new AIProjectClient(projectEndpoint, credential);

// Same two agents, same instructions as Stage 1 - unchanged
AIAgent letterAgent = projectClient.AsAIAgent(
    model: deployment,
    instructions: """
        You are a customer service assistant drafting a professional, empathetic response
        letter to a customer complaint. The complaint text has had personal information
        redacted (shown as placeholders like <person_0>) - do not attempt to fill these in
        or refer to the redaction. Write a concise, courteous letter acknowledging the issue
        and outlining next steps. Do not invent specific compensation amounts or promises
        not implied by the complaint.
        """,
    name: "LetterAgent",
    description: "Drafts a response letter to a customer complaint");

AIAgent evaluatorAgent = projectClient.AsAIAgent(
    model: deployment,
    instructions: """
        You evaluate a draft complaint-response letter against the original complaint.
        Score 1-5 on: tone/empathy, relevance to the complaint, and whether it invents
        any promises or facts not present in the complaint.
        Respond ONLY with JSON: { "score": int, "notes": string }.
        """,
    name: "EvaluatorAgent",
    description: "Scores the draft letter for tone, relevance, and invented facts");

// Same sequential chain as Stage 1 - now converted into a single hostable AIAgent
AIAgent agent = AgentWorkflowBuilder
    .BuildSequential("generate-and-evaluate", letterAgent, evaluatorAgent)
    .AsAIAgent(
        name: "generate-and-evaluate",
        description: "Generates a complaint response letter and evaluates its quality");

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
