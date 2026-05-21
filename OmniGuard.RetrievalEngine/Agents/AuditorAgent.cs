using OmniGuard.RetrievalEngine.Models;

namespace OmniGuard.RetrievalEngine.Agents;

public class AuditorAgent
{
    public AuditorOutput AuditMatches(ResearcherOutput researcherData, string originalPrompt)
    {
        var approvedNodes = new List<RrfScoreTracker>();

        foreach (var tracker in researcherData.RawMatches)
        {
            if (tracker.DenseScore > 0 && tracker.SparseScore == 0)
            {
                bool userAskedForSpecificRule = originalPrompt.Contains("MCOB", StringComparison.OrdinalIgnoreCase) ||
                                                originalPrompt.Contains("COBS", StringComparison.OrdinalIgnoreCase) ||
                                                originalPrompt.Contains("CASS", StringComparison.OrdinalIgnoreCase);

                if (userAskedForSpecificRule)
                {
                    continue;
                }
                tracker.DenseScore *= 0.5f;
            }
            approvedNodes.Add(tracker);
        }

        var finalSelection = approvedNodes.OrderByDescending(x => x.CombinedRrfScore).Take(3).ToList();

        if (finalSelection.Count == 0)
        {
            return new AuditorOutput(false, [], "Compliance Guard Action: The specified rule citation or exact regulatory keywords could not be verified in the active FCA Handbook database index.");
        }

        return new AuditorOutput(true, finalSelection);
    }
}