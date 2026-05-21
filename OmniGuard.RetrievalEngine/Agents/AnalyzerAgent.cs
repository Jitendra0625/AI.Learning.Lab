using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OmniGuard.RetrievalEngine.Models;

namespace OmniGuard.RetrievalEngine.Agents;

public class AnalyzerAgent(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tell", "me", "about", "the", "rule", "what", "is", "are", "show", "find", "please", "section"
    };

    public async Task<AnalyzerOutput> AnalyzeQueryAsync(string userPrompt)
    {
        var alphanumericTokens = Regex.Matches(userPrompt, @"[a-zA-Z0-9]+")
            .Select(m => m.Value)
            .Where(token => !StopWords.Contains(token))
            .Select(token => $"\"{token}\"")
            .ToArray();

        if (alphanumericTokens.Length == 0) return new AnalyzerOutput(string.Empty, []);

        string formattedSqlQuery = string.Join(" AND ", alphanumericTokens);
        var embeddingResult = await embeddingGenerator.GenerateAsync(userPrompt);

        return new AnalyzerOutput(formattedSqlQuery, embeddingResult.Vector.ToArray());
    }
}