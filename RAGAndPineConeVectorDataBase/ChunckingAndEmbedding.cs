using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using Microsoft.SemanticKernel.Text;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.ChatCompletion;
using PineconeSDK = Pinecone;
using Pinecone;
using Microsoft.SemanticKernel.Connectors.OpenAI; // Corrected alias spelling

namespace RAGAndPineConeVectorDataBase
{
    // --- STEP 1: DEFINE THE DATA MODEL CORRECTLY ---
    public class PolicyRecord
    {
        [VectorStoreKey] // Correct attribute name
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Force the SDK to look for the exact "Text" key in Pinecone metadata
        [VectorStoreData(StorageName = "Text")]
        public string Text { get; set; }

        [VectorStoreVector(384)] // Correct attribute name (bge-small uses 384)
        public ReadOnlyMemory<float> Embedding { get; set; }
    }

    internal class ChunckingAndEmbedding
    {
        IKernelBuilder builder = null;
        Kernel kernel;
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = null;
        PineconeCollection<string, PolicyRecord> collection = null;

        public async Task GenerateAndSaveEmbedding()
        {
            // --- STEP 2: PDF READING ---
            string pdftext = string.Empty;
            string path = Path.Combine(AppContext.BaseDirectory, "CompanyPolicy.pdf");
            using (var reader = new PdfReader(path))
            using (var pdfdoc = new PdfDocument(reader))
            {
                for (int page = 1; page <= pdfdoc.GetNumberOfPages(); page++)
                {
                    pdftext += PdfTextExtractor.GetTextFromPage(pdfdoc.GetPage(page));
                }
            }

            // --- STEP 3: CHUNKING ---
#pragma warning disable SKEXP0050 
            var lines = TextChunker.SplitPlainTextLines(pdftext, 200);
            var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, 400);
#pragma warning restore SKEXP0050

            // --- STEP 4: KERNEL & EMBEDDING SETUP ---
            builder = Kernel.CreateBuilder();
            builder.AddBertOnnxEmbeddingGenerator(
                onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
                vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
            );
            kernel = builder.Build();
            embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

            // --- STEP 5: PINECONE SETUP ---
            var apiKey = Environment.GetEnvironmentVariable("Pinecone_APIKey", EnvironmentVariableTarget.User);
            var indexName = Environment.GetEnvironmentVariable("PineConeDBIndexName1", EnvironmentVariableTarget.User) ?? "rag-index";

            // Initialize the SDK client
            var pineconeClient = new PineconeSDK.PineconeClient(apiKey);

            // Initialize the Store (IndexName is NOT passed here anymore)
            var vectorStore = new PineconeVectorStore(pineconeClient);

            // Get the collection and specify the index name here
            collection = vectorStore.GetCollection<string, PolicyRecord>(indexName);

            // --- STEP 6: GENERATE EMBEDDINGS AND SAVE ---
            foreach (var paragraph in paragraphs)
            {
                var embedding = await embeddingGenerator.GenerateAsync(new[] { paragraph });

                var record = new PolicyRecord
                {
                    Text = paragraph,
                    Embedding = embedding[0].Vector // Get the vector from the embedding result
                };

                await collection.UpsertAsync(record); // We can go and check on PineCone dashboard to see the records being added in real-time. Pinecone.io loging there
            }
        }
        public async Task<string> GetSearhResultFromPineConeVectorStore(string query)
        {
            #region Commented Code. Read the Comentry
            // --- STEP 1: KERNEL & EMBEDDING SETUP (same as before) --- oras we don;t want to repeat same we can use the one generated in chunking method and make them class level varibale
            //IKernelBuilder builder = Kernel.CreateBuilder();
            //builder.AddBertOnnxEmbeddingGenerator(
            //    onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx",
            //    vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
            //);
            // Can be redone same nbut as we are in same project I am not repeating same and using class level already genearted kernel, collections


            //var kernel = builder.Build();
            //var embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            //// --- STEP 2: PINECONE SETUP (same as before) ---
            //var apiKey = Environment.GetEnvironmentVariable("Pinecone_APIKey", EnvironmentVariableTarget.User);
            //var indexName = Environment.GetEnvironmentVariable("PineConeDBIndexName1", EnvironmentVariableTarget.User) ?? "rag-index";
            //var pineconeClient = new PineconeSDK.PineconeClient(apiKey);
            //var vectorStore = new PineconeVectorStore(pineconeClient);
            //var collection = vectorStore.GetCollection<string, PolicyRecord>(indexName);

            #endregion  

            // --- STEP 3: GENERATE QUERY EMBEDDING ---
            var queryEmbedding = await embeddingGenerator.GenerateAsync(new[] { query });

            #region This search is not working due to some nuget version mismatch in vector abstranaction and Semantice Kernel
            //// --- STEP 4: SEARCH IN PINECONE ---
            //var searchOptions = new VectorSearchOptions<PolicyRecord>
            //{

            //    IncludeVectors = false // Optional: set to true if you need the vectors back
            //};
            //var searchResults =  collection.SearchAsync(queryEmbedding[0].Vector,3,null); // Get top 3 results

            //// --- STEP 5: RETURN RESULTS ---
            //var resultTexts = new List<string>();
            //var list = await searchResults.ToListAsync();
            //string contextText = string.Join("\n", list.Select(r => r.Record.Text));// combining the retrieved document chunks into a single context string that can be used for generating a response to the user's query. This context will provide the necessary information for the language model to generate an informed answer based on the retrieved documents.

            //// Use await foreach to consume the stream
            //await foreach (var result in searchResults)
            //{
            //    resultTexts.Add(result.Record.Text);
            //}
            #endregion

            #region Trying this to search from Pinecone collection
            // --- STEP 1: GENERATE QUERY EMBEDDING ---
            var queryEmbeddings = await embeddingGenerator.GenerateAsync(new[] { query });
            var queryVector = queryEmbeddings[0].Vector.ToArray(); // Pinecone SDK needs float[]

            // --- STEP 2: BYPASS BROKEN SK CONNECTOR & USE SDK DIRECTLY ---
            // Get the raw index from the SDK client
            // Initialize the SDK client
            string pineConeApiKey = Environment.GetEnvironmentVariable("Pinecone_APIKey", EnvironmentVariableTarget.User);
            string pineConeIndexName = Environment.GetEnvironmentVariable("PineConeDBIndexName1", EnvironmentVariableTarget.User) ?? "rag-index";
            var pineconeClient = new PineconeSDK.PineconeClient(pineConeApiKey);
            var index = pineconeClient.Index(pineConeIndexName);

            // Query the index directly via Pinecone's own API
            var request = new QueryRequest
            {
                Vector = queryVector,
                TopK = 3,
                IncludeMetadata = true// This ensures the "Text" field comes back
            };
            var queryResponse = await index.QueryAsync(request);
            // 5.Extract the text
            var resultTexts = new List<string>();
            foreach (var match in queryResponse.Matches)
            {
                if (match.Metadata != null && match.Metadata.TryGetValue("Text", out var textValue))
                {
                    // Metadata values are stored in the 'Inner' property
                    resultTexts.Add(textValue?.ToString() ?? "");
                }
            }

            return string.Join("\n\n", resultTexts);
            #endregion
        }

        public async Task<string> GetResponseFromLLMAfterAugmentation(string query)
        {
            // This method would take the user query, retrieve relevant context from Pinecone (using the previous method), and then call an LLM to generate a response based on that context. 
            // For example, you could use the retrieved context to create a prompt for GPT-4 or any other language model, and then return the generated answer to the user.
            // This is where you would integrate with your LLM of choice, passing in the retrieved context and the original query to generate a final response.

            string context = await GetSearhResultFromPineConeVectorStore(query);
            string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  // Or any Chat-optimized model
            string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
            //Build the orchetration for LLM to generate resposne based on the retrieved context and user query. You can use any LLM provider like OpenAI, Azure OpenAI, HuggingFace etc. Here is a pseudo code for how you might do this:#
            IKernelBuilder builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion(modelId: modelId ?? "" //, we can use any chat-optimized model from Hugging Face for the chat completion part of our RAG system, means this chat completion LLM will respisne based on our own data which we will provide from vector store
                    , apiKey: apiKey,
                    endpoint: new Uri("https://router.huggingface.co/v1")); // Example with OpenAI, replace with HuggingFace or other provider as needed
            var kernel = builder.Build();
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var settings = new OpenAIPromptExecutionSettings()
            {
                //MaxTokens = 500,
                Temperature = 0.1F
            };
            var history = new ChatHistory();
            history.AddSystemMessage("You are an HR assistant. Answer the question ONLY using the provided context.");
            history.AddUserMessage($"Context: {context}\n\nQuestion: {query}");

            // 3. GENERATE: Get the final AI response
            var result = await chatService.GetChatMessageContentAsync(history,executionSettings: settings,
            kernel: kernel);//  Use Kernel.InvokePrompt for quick one-off answer and GetChatMessageContentAsync for more complex conversation with memory and system prompt. This will generate the answer based on the context we retrieved from vector store and the user query.
                            //  If the context does not contain relevant information, it will respond accordingly.

            return result.ToString();
        }
    }
}