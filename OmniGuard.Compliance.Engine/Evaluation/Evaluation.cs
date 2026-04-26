using OmniGuard.Compliance.Engine.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OmniGuard.Compliance.Engine.Evaluation
{
    internal class EvaluationService
    {
        private readonly RetrievalService _retrivalService;
        public EvaluationService(RetrievalService retrivalService)
        {
            _retrivalService = retrivalService;
        }
        public async Task RunEvaliuationSuiteAsync()
        {
            // Load your 1-200 page test cases
            var testCases = await LoadDatasetAsync();
            var results = new List<EvaluationResult>();

            foreach (var test in testCases.TestCases)
            {

                // This handles: Vector Search -> Context Retrieval -> Auditor/Judge
                var response = await _retrivalService.GetFinalResponseAsync(test.Question);

                // Benchmark against the Golden Dataset
                results.Add(new EvaluationResult
                {
                    Id = test.Id,
                    Match = (response.RetrievedPages.Contains(test.ExpectedPage)), // Did we find the right page?
                    Confidence = response.Confidence, // Did the Judge agree it was high quality?
                    Reasoning = response.AuditorReasoning,
                    TestQuestion=test.Question,
                    TestType=test.Type,
                    RetrievedPages=response.RetrievedPages,
                    ExpectedPage= test.ExpectedPage
                });
            }


            PrintResult(results);
        }

        public async Task<GoldenDataset> LoadDatasetAsync()
        {
            try
            {
                string filePath = Path.Combine(AppContext.BaseDirectory, @"TestData\TestData.json");
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Could not find golden dataset at: {filePath}");
                }

                var jsonContent = await File.ReadAllTextAsync(filePath);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                return  JsonSerializer.Deserialize<GoldenDataset>(jsonContent,options)
                       ?? throw new InvalidOperationException("Failed to deserialize golden dataset.");
            }
            catch (Exception ex)
            {

            }
            return null; 
        }

        internal void PrintResult(List<EvaluationResult> results)
        {
            foreach (var result in results)
            {
                if (result.Confidence == "Low")
                    Console.ForegroundColor = ConsoleColor.Red;
                if (result.Confidence == "Medium")
                    Console.ForegroundColor = ConsoleColor.Yellow;
                if (result.Confidence == "High")
                    Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"confidence is {result.Confidence}, for test case {result.Id}, it's a match {result.Match}, reasoning is {result.Reasoning}");
                Console.ResetColor();
            }
            //    Console.Clear();
            //    Console.WriteLine("=========================================================");
            //    Console.WriteLine("OMNIGUARD.COMPLIANCE.ENGINE - WAVE 5 BENCHMARK");
            //    Console.WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            //    Console.WriteLine("=========================================================\n");

            //    foreach (var res in results)
            //    {

            //        string pages = string.Join(", ", res.RetrievedPages);
            //        Console.WriteLine($"[{res.Id}] Type: {res.TestType}");
            //        Console.WriteLine($"   Question: {res.TestQuestion}");
            //        Console.WriteLine($"   Expected: {res.ExpectedPage} | Found: [{pages}]");
            //        Console.WriteLine($"   Auditor Confidence: {res.Confidence}");
            //        Console.WriteLine($"   Reason: {res.Reasoning}");
            //        Console.WriteLine(new string('-', 40));
            //    }

            //    // --- METRIC CALCULATIONS ---
            //    int total = results.Count;
            //    int success = results.Count(r => r.IsSuccess);
            //    int retrievalHits = results.Count(r => r.TestType == "Positive" && r.Match);
            //    int hallucinationBlocks = results.Count(r => r.TestType == "Negative" && r.IsSuccess);

            //    double accuracy = (double)success / total * 100;
            //    double contextPrecision = (double)retrievalHits / results.Count(r => r.TestType == "Positive") * 100;

            //    Console.WriteLine("\nFINAL PERFORMANCE SUMMARY");
            //    Console.WriteLine("---------------------------------------------------------");
            //    Console.WriteLine($"Overall System Accuracy:   {accuracy:F1}%");
            //    Console.WriteLine($"Context Precision (Top-K): {contextPrecision:F1}%");
            //    Console.WriteLine($"Hallucination Rejection:  {hallucinationBlocks} / {results.Count(r => r.TestType == "Negative")} cases");
            //    Console.WriteLine("---------------------------------------------------------");
            //}

        }
        }

    internal class EvaluationResult
    {
        public string Id;
        public int ExpectedPage;
        public bool Match;
        public string Confidence;
        public string Reasoning;
        public string TestQuestion;
        public string TestType;
        public List<int> RetrievedPages;
        public bool IsSuccess => TestType switch
        {
            // For Positive tests: We must find the EXACT page AND the Auditor must be confident.
            "Positive" => Match && Confidence == "High",

            // For Negative tests: We SUCCESS if the Auditor is NOT High (Medium/Low).
            // It means the "Compliance Firewall" worked and blocked a hallucination.
            "Negative" => Confidence != "High",

            _ => false
        };
        public string StatusIcon => IsSuccess ? "✅" : "❌";

    }
    public record GoldenDataset(
    string Project,
    string EvaluationScope,
    List<TestCases> TestCases
);

    public record TestCases(
        string Id,
        string Question,
        int ExpectedPage,
        string Type, // "Positive" or "Negative"
        string Note
    );
}
