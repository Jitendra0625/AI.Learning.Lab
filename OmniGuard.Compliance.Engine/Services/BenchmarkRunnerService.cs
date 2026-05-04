using OmniGuard.Compliance.Engine.Services;
using System.Diagnostics;

internal class BenchmarkRunnerService
{
    private readonly RetrievalService _semanticOnly; // Targets doc_fca_
    private readonly HybridRetrievalService _hybrid; // Targets doc_fcahybrid_

    public BenchmarkRunnerService(RetrievalService semantic, HybridRetrievalService hybrid)
    {
        _semanticOnly = semantic;
        _hybrid = hybrid;
    }

    public async Task RunStressTestAsync()
    {
        // Define our 'Gold Standard' - Query vs the Page it SHOULD find
        var cases = new List<(string Query, string TargetPage)>
        {
             ("MCOB 3A.2.1R fair clear and not misleading", "67"), 
            ("MCOB 4.4A.1R initial disclosure requirements", "87"), 
            ("MCOB 1.3 Territorial scope where MCOB applies", "22"), 
            ("MCOB 3A.1.13 Financial promotion territorial scope", "65"), 
            ("Rules for home purchase plans sales standards", "117") 
        };

        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine($"{"QUERY",-40} | {"SEMANTIC",-10} | {"HYBRID",-10} | {"WINNER"} ");
        Console.WriteLine(new string('-', 80));

        foreach (var test in cases)
        {
            var sw = Stopwatch.StartNew();

            // Run both in parallel for speed
            var semTask = _semanticOnly.GetComplianceAnswerAsync(test.Query);
            var hybTask = _hybrid.GetComplianceAnswerAsync(test.Query);
            await Task.WhenAll(semTask, hybTask);

            // Verify Hits
            bool semHit = semTask.Result.pageNumbers.Any(p => p.Equals(Convert.ToInt32($"{test.TargetPage}")));
            bool hybHit = hybTask.Result.pageNumbers.Any(p => p.Equals(Convert.ToInt32($"{test.TargetPage}")));

            string winner = (hybHit && !semHit) ? "HYBRID RECOVERY" : (hybHit ? "STABLE" : "BOTH MISS");

            Console.WriteLine($"{test.Query.PadRight(40).Substring(0, 40)} | {(semHit ? "PASS" : "FAIL"),-10} | {(hybHit ? "PASS" : "FAIL"),-10} | {winner}");
        }
        Console.WriteLine(new string('=', 80));
    }
}