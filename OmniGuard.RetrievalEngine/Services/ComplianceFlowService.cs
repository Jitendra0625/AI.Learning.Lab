using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenQA = Microsoft.SemanticKernel.Connectors.OpenAI;
using OmniGuard.RetrievalEngine.Plugins;
using OmniGuard.RetrievalEngine.Models;

namespace OmniGuard.RetrievalEngine.Services;

public class ComplianceFlowService(Kernel kernel, PolicySearchPlugin policySearchPlugin)
{
    public async Task<ComplianceApiResponse> RunComplianceFlowAsync(string userQuery)
    {
        // Add the Policy Search tool as a native plugin to the agent kernel context
        kernel.Plugins.AddFromObject(policySearchPlugin, "PolicySearch");

        // 1. Define the Analyzer Agent
        ChatCompletionAgent analyzer = new()
        {
            Name = "Analyzer",
            Instructions = """
                You are a Compliance Query Analyzer. 
                1. Your sole task is to isolate and extract strict FCA Handbook rules, abbreviations, and citation codes from the user's prompt (e.g., 'MCOB 2.2.6R', 'CASS 7').
                2. Strip away all conversational fluff, padding, and framing words (e.g., 'please find', 'tell me about', 'what is').
                3. Output only the clean, high-value regulatory keywords separated by spaces. Do not write full sentences.
                """,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenQA.OpenAIPromptExecutionSettings
            {
                ServiceId = "HuggingFaceChat"
            })
        };

        // 2. Define the Researcher Agent
        ChatCompletionAgent researcher = new()
        {
            Name = "Researcher",
            Instructions = """
                You are a Legal Researcher specialized in the FCA Handbook.
                1. Always use the PolicySearch_SearchPolicyKnowledgebase tool to fetch verified handbook text using the sanitized keywords provided by the Analyzer.
                2. Do not attempt to guess or answer from your internal training weights. You must run the search tool.
                3. Once you have the raw document context results, summarize the evidence clearly for the Compliance_Auditor.
                4. You MUST provide a response after the tool call is completed.
                """,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenQA.OpenAIPromptExecutionSettings
            {
                // FIXED: Resolved from global Microsoft.SemanticKernel namespace
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                ServiceId = "HuggingFaceChat"
            })
        };

        // 3. Define the Auditor Agent
        ChatCompletionAgent auditor = new()
        {
            Name = "Compliance_Auditor",
            Instructions = """
                You are a Senior Bank Auditor. 
                1. Review the raw evidence summary provided by the Researcher.
                2. Assign a Confidence Score strictly in this format: [High/Medium/Low].
                3. Provide a 'Compliance Advisory' if the evidence is missing, out-of-date, or unclear.
                4. If the exact rule citation requested is present and verified in the text context, assign a [High] score and say 'AUDIT PASSED'.
                5. If confidence is [Low] or [Medium], state that the rule could not be verified and say 'AUDIT FAILED'.
                """,
            Kernel = kernel
        };

        // Initialize Group Chat space for history orchestration tracking
#pragma warning disable SKEXP0110
        AgentGroupChat chat = new(analyzer, researcher, auditor);
#pragma warning restore SKEXP0110

        chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, userQuery));

        // ==========================================
        // STEP 1: EXECUTE ANALYZER
        // ==========================================
        var analyzerOutput = new System.Text.StringBuilder();
        var historyForAnalyzer = await chat.GetChatMessagesAsync().ToListAsync();

        await foreach (var message in analyzer.InvokeAsync(historyForAnalyzer))
        {
            analyzerOutput.AppendLine(message.Message.Content);
        }

        string sanitizedQuery = analyzerOutput.ToString().Trim();
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, $"Sanitized Query Keywords: {sanitizedQuery}") { AuthorName = "Analyzer" });

        // ==========================================
        // STEP 2: EXECUTE RESEARCHER
        // ==========================================
        var researcherOutput = new System.Text.StringBuilder();
        var historyForResearcher = await chat.GetChatMessagesAsync().ToListAsync();

        await foreach (var message in researcher.InvokeAsync(historyForResearcher))
        {
            researcherOutput.AppendLine(message.Message.Content);
        }

        string researcherSummary = researcherOutput.ToString().Trim();
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, researcherSummary) { AuthorName = "Researcher" });

        // ==========================================
        // STEP 3: EXECUTE AUDITOR
        // ==========================================
        var auditorOutput = new System.Text.StringBuilder();
        var historyForAuditor = await chat.GetChatMessagesAsync().ToListAsync();

        await foreach (var message in auditor.InvokeAsync(historyForAuditor))
        {
            auditorOutput.AppendLine(message.Message.Content);
        }

        string finalVerdict = auditorOutput.ToString().Trim();
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, finalVerdict) { AuthorName = "Compliance_Auditor" });

        // Map data directly to the Web API response contract
        return new ComplianceApiResponse(
            Status: finalVerdict.Contains("AUDIT PASSED") ? "Verified" : "Rejected",
            SanitizedQuery: sanitizedQuery,
            ResearcherSummary: researcherSummary,
            AuditorVerdict: finalVerdict
        );
    }
}