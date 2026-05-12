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
        var cases = new List<(string Query, string TargetPage, bool ShouldExist)>
        {
            // ("MCOB 3A.2.1R fair clear and not misleading", "67"), 
            //("MCOB 4.4A.1R initial disclosure requirements", "87"), 
            //("MCOB 1.3 Territorial scope where MCOB applies", "22"), 
            //("MCOB 3A.1.13 Financial promotion territorial scope", "65"), 
            ////("Rules for home purchase plans sales standards", "117") 
            ("MCOB 3A.2.1R fair clear and not misleading", "67", true),
            ("MCOB 4.4A.1R initial disclosure requirements", "87", true),
            ("MCOB 1.3 Territorial scope where MCOB applies", "22", true),
            ("MCOB 3A.1.13 Financial promotion territorial scope", "65", true),
            ("Rules for home purchase plans sales standards", "117", true),
            ("MCOB 2.2.6R reliance on others for information", "35", false), // THE GHOST MATCH
            ("MCOB 3A.4.4 approval of financial promotions", "71", true),
            ("MCOB 11.6.2R responsible lending assessment", "104", false),// THE GHOST MATCH
            ("MCOB 13.5.1R communication with customers in arrears", "114", false),// THE GHOST MATCH
        };

        Console.WriteLine("\n" + new string('=', 95));
        Console.WriteLine($"{"QUERY",-45} | {"SEMANTIC",-10} | {"HYBRID",-10} | {"INTEGRITY STATUS"} ");
        Console.WriteLine(new string('-', 95));

        foreach (var test in cases)
        {
            var semTask = _semanticOnly.GetComplianceAnswerAsync(test.Query);
            var hybTask = _hybrid.GetComplianceAnswerAsync(test.Query);
            await Task.WhenAll(semTask, hybTask);

            bool semHit = semTask.Result.pageNumbers.Contains(Convert.ToInt32(test.TargetPage));
            bool hybHit = hybTask.Result.pageNumbers.Contains(Convert.ToInt32(test.TargetPage));

            // Logic for the "Ghost Match" scenario
            string integrity;
            if (!test.ShouldExist)
            {
                // If it shouldn't exist, HYBRID is a PASS if it REJECTED the result (hybHit is false)
                integrity = (!hybHit) ? "REJECTED (SAFE)" : "HALLUCINATED";
            }
            else
            {
                integrity = hybHit ? "ACCURATE" : "MISS";
            }

            Console.WriteLine($"{test.Query.PadRight(45).Substring(0, 45)} | {(semHit ? "PASS" : "FAIL"),-10} | {(hybHit ? "PASS" : "FAIL"),-10} | {integrity}");
        }
    }
}