using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Text;// for chunking

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using Microsoft.ML.OnnxRuntimeGenAI;
using Pinecone;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using iText.Commons.Json;

namespace OmniRAG.Core
{
    internal class ChunkingAndEmbedding
    {
        IKernelBuilder builder;
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
        PineconeCollection<string, PolicyRecords> collection = null;
        PineconeClient pineConeClient;
        string indexName;
        public async Task GetAndSaveVectors()
        {
            string pdfText = string.Empty;
            string path = Path.Combine(AppContext.BaseDirectory, "Global Tech Solutions.pdf");
            using (var pdf = new PdfReader(path))
            using (var doc = new PdfDocument(pdf))
            {
                for (int i = 1; i <= doc.GetNumberOfPages(); i++)
                {
                    pdfText += PdfTextExtractor.GetTextFromPage(doc.GetPage(i));
                }
            }


            // do chunking Not using text chunker as my data has headers and want to split data based on header
            //#pragma warning disable SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            //            var lines = TextChunker.SplitPlainTextLines(pdfText, 100);


            //            var paraGraphs = TextChunker.SplitPlainTextParagraphs(lines, 150);
            //#pragma warning restore SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.



            // Embedding
            builder = Kernel.CreateBuilder();
            builder.AddBertOnnxEmbeddingGenerator(
                onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
                vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
            );
            Kernel kernel = builder.Build();
            embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

            // --- STEP 5: PINECONE SETUP ---
            var apiKey = Environment.GetEnvironmentVariable("Pinecone_APIKey", EnvironmentVariableTarget.User);
            indexName = Environment.GetEnvironmentVariable("PineConeDBIndexName2", EnvironmentVariableTarget.User) ?? "rag-index2";
            pineConeClient = new PineconeClient(apiKey);
            var vectorStroe = new PineconeVectorStore(pineConeClient);
            collection = vectorStroe.GetCollection<string, PolicyRecords>(indexName);
            // Use Regex or String Splitting to find the[Category: X | Year: Y] tag in the paragraph

            // 1. Clean the text slightly (remove odd line breaks)
            string cleanedText = pdfText.Replace("\r", "").Replace("\n", " ");

            // 2. Split by the [Category header
            // This ensures every item in 'sections' starts with "[Category:"
            var sections = Regex.Split(cleanedText, @"(?=\[Category:)")
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();

            // 3. Loop through sections, extract metadata, and save
            foreach (var section in sections)
            {
                // Extract Metadata using Regex
                var metaMatch = Regex.Match(section, @"\[Category:\s*(?<cat>.*?)\s*\|\s*Year:\s*(?<year>\d{4})\]");

                string category = metaMatch.Success ? metaMatch.Groups["cat"].Value : "General";
                int year = metaMatch.Success ? int.Parse(metaMatch.Groups["year"].Value) : 2024;

                // Generate Embedding for this specific section
                var embeddingResult = await embeddingGenerator.GenerateAsync(new[] { section });

                // Create the Record with the metadata
                var record = new PolicyRecords
                {
                    Id = Guid.NewGuid().ToString(),
                    Text = section.Trim(),
                    Category = category,
                    Year = year,
                    Embedding = embeddingResult[0].Vector // Map the first vector
                };
                // await collection.UpsertAsync(record); // commented this temporary as data is already in pinecone now and don't want to create again and agani. The stroing will be in deiffernt model then searching
            }
           // await SearchResult();
        }


        /// <summary>
        /// This methid will now search the context/ related data based on the cosine similarrity of vecotrs
        /// and the filter we passed to seleced more relevent data from lard amount of data store in vecotr data store
        /// </summary>
        /// <param name="filters"></param>
        /// <returns></returns>
        public async Task SearchResult(SearchIntent filters)
        {
            GetAndSaveVectors(); // The vectors are alreay stored in database by running this once but calling it here to use the embedding, pinecone obejects created 
            // in this method. Ideally this should be a different pmodule to read, chunk and store and then searching , responding shud be
            // differnet. As I am in learning  and being lazy :) so not doing it now
            // --- STEP 1: VECTORIZE THE USER QUERY ---
            //var query = query;
            var queryEmbeddings = await embeddingGenerator.GenerateAsync(new[] { filters.RefinedQuery });
            float[] queryVector = queryEmbeddings[0].Vector.ToArray();

            // --- STEP 2: BUILD THE FILTERED REQUEST ---
            var request = new QueryRequest
            {
                Vector = queryVector,
                TopK = 3,
                IncludeMetadata = true,

                // 🔥 THE FILTER: This tells Pinecone: "Only look at 2024 HR documents" and nothing else. filter the vector search when there are different type of categories infirmation 
                Filter = new Metadata 
                {
                    ["Category"] = filters.Category,// "HR",
                    ["Year"] = filters.Year //2023 // In Pinecone if year saved as string, this will not bring the seacrh. And it will be an and condition searched for each parameter here
                }
            };

            // --- STEP 3: EXECUTE & EXTRACT ---
            var index = pineConeClient.Index(indexName);
            var queryResponse = await index.QueryAsync(request);

            if (queryResponse.Matches.Count() == 0)
            {
                Console.WriteLine($"System Note: No data found for {filters.Category} in {filters.Year}.");
                Console.WriteLine("AI Response: I'm sorry, I don't have any policy records for that specific category or year.");
                return;
            }

            foreach (var match in queryResponse.Matches)
            {
                //// Access the 'Text' metadata we stored during ingestion
                //var text = match.Metadata["Text"]?.ToString();
                ////var category = match.Metadata["Category"]?.ToString();
                ////var year = match.Metadata["Year"]?.ToString();

                ////Console.WriteLine($"[Match Found - Category: {category}, Year: {year}]");
                ////Console.WriteLine($"Content: {text}");
                ////Console.WriteLine("-----------------------------------");

                string context = match.Metadata["Text"].ToString();
                // Now pass this context to the final LLM response...
                await GenerateFinalAnswerAsync(filters.RefinedQuery, context);
            }
        }

        public async Task GenerateFinalAnswerAsync(string query, string context)
        {

            IKernelBuilder builder = Kernel.CreateBuilder();
            string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  // Or any Chat-optimized model
            string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
            builder.AddOpenAIChatCompletion(
                modelId: modelId,
                apiKey: apiKey,
                endpoint: new Uri("https://router.huggingface.co/v1"));
            var kernel = builder.Build();
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.4F
            };
            var systemPrompt = @$"
            You are a helpful assistant. Answer the question using ONLY the provided context.
            If the answer isn't in the context, say you don't know.

            Context: {context}
            User Question: {query}";

            var response = await chatService.GetChatMessageContentAsync(systemPrompt, settings);
            Console.WriteLine($"\nAI Response: {response.Content}");
        }
        /// <summary>
        /// This method will extract the filters from the user query first and then we will pass the extracted
        /// filtes in the search from vector to get the context and then again we will pass that extracted context to chat llm againb#
        /// to get the response
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public async Task<SearchIntent> GetSearchIntentAsync(string query)
        {

            IKernelBuilder builder = Kernel.CreateBuilder();
            string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  // Or any Chat-optimized model
            string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
            builder.AddOpenAIChatCompletion(
                modelId: modelId,
                apiKey: apiKey,
                endpoint: new Uri("https://router.huggingface.co/v1"));
            var kernel = builder.Build();
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.4F
            };
            var systemPrompt = @"
                You are a search assistant. Extract the Category and Year from the user's query.
                Categories: [HR, Finance, IT, Legal]. Default Year: 2024.
                Respond ONLY in JSON. 
                Example: { 'Category': 'HR', 'Year': 2023, 'RefinedQuery': 'remote work' }";
            var response = await chatService.GetChatMessageContentAsync(systemPrompt + query, settings);
            // Clean the invalid character from json response of there is 
            // Remove markdown code blocks if the AI added them
            string cleanJson = response.Content
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();
            return JsonSerializer.Deserialize<SearchIntent>(cleanJson);
        }
    }
}

