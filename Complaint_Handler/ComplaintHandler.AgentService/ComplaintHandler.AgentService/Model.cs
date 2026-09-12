namespace ComplaintHandler.AgentService;

/// <summary>
/// Incoming request from GenerateLetter (Functions app). Mirrors the text
/// GenerateLetter has already run through Prompt Shields + Content Safety pre-check —
/// this service assumes the input has already cleared those gates.
/// </summary>
public record GenerateAndEvaluateRequest(string ComplaintId, string RedactedText);

/// <summary>
/// Response shape kept intentionally identical to the fields GenerateLetter already
/// expects from the in-process version, so the swap is a pure call-site change.
/// </summary>
public record GenerateAndEvaluateResponse(
    string DraftLetter,
    int EvaluationScore,
    string EvaluationNotes
);

/// <summary>
/// Shape the EvaluatorAgent is instructed to return as JSON. Renamed from the
/// original "EvaluationResult" name — that name collides with
/// Microsoft.Extensions.AI.Evaluation.EvaluationResult, pulled in transitively
/// by the Agent Framework packages (a real build error hit in Session 6/7).
/// </summary>
public record LetterEvaluationResult(int Score, string Notes);