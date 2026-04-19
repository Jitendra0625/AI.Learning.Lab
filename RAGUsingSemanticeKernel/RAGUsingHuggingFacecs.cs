using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace RAGUsingSemanticeKernel
{
    internal class RAGUsingHuggingFacecs
    {
        private InMemoryCollection<Guid, DocChunk> collection = null;
        private IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = null;
        private Kernel kernel = null;


        //string embeddingModelId = "sentence-transformers/all-MiniLM-L6-v2"; // A popular embedding model from Hugging Face
        string? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);  // Or any Chat-optimized model
        string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
        public async Task GenerateEmbeddignandVectorStore()
        {
            try
            {
                IKernelBuilder builder = Kernel.CreateBuilder();
                // to generate embeddings for our documents, we can use the Hugging Face embedding generator plugin. This plugin allows us to generate embeddings using Hugging Face models, which can then be stored in a vector database for efficient retrieval during the RAG process.
                builder.AddBertOnnxEmbeddingGenerator(
                    onnxModelPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\model.onnx", //onxx file needed for semantic kernel
                    vocabPath: @"C:\AgenticAI\LocalModels\bge-small-en-v1.5\vocab.txt"
                    )
                    .AddOpenAIChatCompletion(modelId: modelId ?? "" //, we can use any chat-optimized model from Hugging Face for the chat completion part of our RAG system, means this chat completion LLM will respisne based on our own data which we will provide from vector store
                    , apiKey: apiKey,
                    endpoint: new Uri("https://router.huggingface.co/v1"));
                kernel = builder.Build();

                // We can now use the kernel to generate embeddings for our documents and store them in a vector database. This will allow us to efficiently retrieve relevant information during the RAG process based on the user's query.
                embeddingGenerator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();


                // setup in-memory vector store
                var vectorStore = new InMemoryVectorStore();
                // Example document chunks to be stored in the vector database
                collection = vectorStore.GetCollection<Guid, DocChunk>("documents");
                await collection.EnsureCollectionExistsAsync();

                // Injecting some sample documents into the vector store with their corresponding embeddings
                // 4. INGEST: Process your .txt file
                string path = Path.Combine(AppContext.BaseDirectory, "hrpolicy.txt");
                string content = string.Empty;
                content = File.ReadAllText(path);
                var chunks = content.Split("\r\n");// For this sample we are splitting the document into chunks based on double newlines, but in a real application you might want to use a more sophisticated method for chunking the text (e.g., by paragraphs or sentences) but there are other ways to chnk the data
                foreach (var chunk in chunks)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(chunk))
                        {
                            continue; // Skip empty chunks
                        }
                        var embedding = await embeddingGenerator.GenerateAsync(new[] { chunk });
                        var vector = embedding[0].Vector; // Access the embedding vector directly
                        var docChunk = new DocChunk
                        {
                            Id = Guid.NewGuid(),
                            Text = chunk,
                            Embedding = vector
                        };
                        await collection.UpsertAsync(docChunk);
                    }
                    catch (Exception ex)
                    {

                        continue; // Skip this chunk and continue with the next one
                    }


                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up kernel or vector store: {ex.Message}");
            }
        }

        internal async Task<string> GetSearhResultFromInMemoryVectorStore(string query)
        {
            try
            {
                // 5. RAG: Search and Generate
                string userQuery = query;// "What is the holiday carry-over policy?";
                var queryVector = await embeddingGenerator.GenerateAsync(new[] { userQuery }); // convert query in embedding so that we can search the similar vectors in vector store
                // 1. Define search options with the record type
                var searchOptions = new VectorSearchOptions<DocChunk>
                {
                    IncludeVectors = false // Optional: set to true if you need the vectors back
                };
                var searchResults = collection.SearchAsync(queryVector[0].Vector, 3, searchOptions);// searching in vector store, means retriveal
                var list = await searchResults.ToListAsync();
                string contextText = string.Join("\n", list.Select(r => r.Record.Text));// combining the retrieved document chunks into a single context string that can be used for generating a response to the user's query. This context will provide the necessary information for the language model to generate an informed answer based on the retrieved documents.
                var result = await kernel.InvokePromptAsync($"""
            Use the context to answer. If not found, say you don't know.
            Context: {contextText}
            Question: {userQuery}
            """);//  Use Kernel.InvokePrompt for quick one-off answer and GetChatMessageContentAsync for more complex conversation with memory and system prompt. This will generate the answer based on the context we retrieved from vector store and the user query. If the context does not contain relevant information, it will respond accordingly.

                return Convert.ToString(result);
            }
            catch (Exception)
            {
                throw;
            }
        }
        // 1. Setup Data Model
        public class DocChunk
        {
            [VectorStoreKey] public Guid Id { get; set; }
            [VectorStoreData] public string Text { get; set; }
            [VectorStoreVector(384)] // all-MiniLM-L6-v2 uses 384 dimensions
            public ReadOnlyMemory<float> Embedding { get; set; }
        }

    }
}
