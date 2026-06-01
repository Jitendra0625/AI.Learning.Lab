using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenQA = Microsoft.SemanticKernel.Connectors.OpenAI;
using OmniGuard.RetrievalEngine.Plugins;
using OmniGuard.RetrievalEngine.Models;
using System.Diagnostics; // ◄ Required for native .NET OpenTelemetry Activity tracking

namespace OmniGuard.RetrievalEngine.Services;

public class ComplianceFlowService
{
    private readonly Kernel _kernel;
    private readonly PolicySearchPlugin _policySearchPlugin;

    // Define the ActivitySource identifier that matches Program.cs registration exactly
    private static readonly ActivitySource OmniGuardSource = new("OmniGuard-Local-Engine");

    public ComplianceFlowService(Kernel kernel, PolicySearchPlugin policySearchPlugin)
    {
        _kernel = kernel;
        _policySearchPlugin = policySearchPlugin;
    }

    public async Task<ComplianceApiResponse> RunComplianceFlowAsync(string userQuery)
    {
        // Capture the parent web host activity context or spin up an isolated execution root span
        using var activity = Activity.Current ?? OmniGuardSource.StartActivity("RunComplianceFlow");

        // Clone the execution kernel per request to guarantee absolute history isolation
        var executionKernel = _kernel.Clone();
        executionKernel.Plugins.AddFromObject(_policySearchPlugin, "PolicySearch");

        // Streamline execution parameters. Temperature 0 guarantees speed and accuracy.
        var researcherSettings = new OpenQA.OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            ServiceId = "HuggingFaceChat",
            Temperature = 0.0,
            MaxTokens = 1000 // Caps generation limits to prevent model loops
        };

        var auditorSettings = new OpenQA.OpenAIPromptExecutionSettings
        {
            ServiceId = "HuggingFaceChat",
            Temperature = 0.0,
            MaxTokens = 300
        };

        // 1. Define the Researcher Agent
        ChatCompletionAgent researcher = new()
        {
            Name = "Researcher",
            Instructions = """
                You are a high-speed Legal Data Retrieval Pipe. 
                1. Always use the PolicySearch_SearchPolicyKnowledgebase tool to fetch verified handbook text. Pass the user's query directly to the tool.
                2. Do not attempt to synthesize, explain, or write a lengthy summary of the rules. 
                3. Your sole responsibility is to print the raw text blocks and metadata blocks exactly as retrieved from the database tool so they can be reviewed by the Auditor. Keep your written commentary to a bare minimum.
                """,
            Kernel = executionKernel,
            Arguments = new KernelArguments(researcherSettings)
        };

        // 2. Define the Auditor Agent
        ChatCompletionAgent auditor = new()
        {
            Name = "Compliance_Auditor",
            Instructions = """
                You are a high-speed Senior Bank Auditor Agent. Be extremely concise. Do not chat.
                
                CRITICAL EVALUATION STEPS:
                1. Analyze the context provided in the system arguments. 
                2. If the exact rule citation requested is fully present and verified in the source text blocks, output exactly:
                   Confidence Score: [High]
                   AUDIT PASSED
                   
                3. If any core requirement from the Target Query is missing, unverified, or flagged with a penalty, output exactly:
                   Confidence Score: [Medium]
                   AUDIT FAILED
                   Reason: State exactly what text component was missing from the source documentation.
                """,
            Kernel = executionKernel,
            Arguments = new KernelArguments(auditorSettings)
        };

        // ==========================================
        // STEP 1: EXECUTE RESEARCHER (Direct Data Retrieval)
        // ==========================================
        var researcherOutput = new System.Text.StringBuilder();
        var researcherHistory = new List<ChatMessageContent> { new(AuthorRole.User, userQuery) };

        await foreach (var message in researcher.InvokeAsync(researcherHistory))
        {
            if (!string.IsNullOrEmpty(message.Message.Content))
            {
                researcherOutput.Append(message.Message.Content);
            }
        }
        string researcherSummary = researcherOutput.ToString().Trim();

        // --- TASK 1 INTERCEPTION: INJECT INPUT & RETRIEVAL TAGS ---
        if (activity != null)
        {
            // Line 1: Track the exact incoming user request string parameters
            activity.SetTag("gen_ai.prompt", userQuery);

            // Line 2: Map the raw text extracted from SQL/Pinecone straight into the RAG context field
            activity.SetTag("rag.context", researcherSummary);
        }

        // ==========================================
        // STEP 2: EXECUTE AUDITOR (Direct Pipeline Coupling)
        // ==========================================
        var auditorOutput = new System.Text.StringBuilder();

        var structuredAuditorInput = new List<ChatMessageContent>
        {
            new(AuthorRole.User, $"""
                Target Query to Evaluate: {userQuery}
                Source Evidence Provided: {researcherSummary}
                """)
        };

        await foreach (var message in auditor.InvokeAsync(structuredAuditorInput))
        {
            if (!string.IsNullOrEmpty(message.Message.Content))
            {
                auditorOutput.Append(message.Message.Content);
            }
        }
        string finalVerdict = auditorOutput.ToString().Trim();

        // --- TASK 1 INTERCEPTION: INJECT OUTPUT COMPLETION TAG ---
        if (activity != null)
        {
            // Line 3: Capture the final deterministic auditor response evaluation text
            activity.SetTag("gen_ai.completion", finalVerdict);
        }

        // Return the extracted research summary directly back to the API contract response 
        return new ComplianceApiResponse(
            Status: finalVerdict.Contains("AUDIT PASSED") ? "Verified" : "Rejected",
            SanitizedQuery: "Routed Directly to PolicySearch Engine",
            ResearcherSummary: researcherSummary,
            AuditorVerdict: finalVerdict
        );
    }
}