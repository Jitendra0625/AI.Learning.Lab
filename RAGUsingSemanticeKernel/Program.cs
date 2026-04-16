using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using RAGUsingSemanticeKernel;
using System.Net;

try
{
    RAGUsingHuggingFacecs ragUsingHuggingFacecs = new RAGUsingHuggingFacecs();
    await ragUsingHuggingFacecs.GenerateEmbeddignandVectorStore();// Generate Embedding and store in the in memoy vecotr store
    Console.WriteLine("Please enter your query: ");
    string? query = Console.ReadLine();// User query
    string response = await ragUsingHuggingFacecs.GetSearhResultFromInMemoryVectorStore(query);
    Console.WriteLine($"{response}");
}

catch (Exception ex)
{
    Console.WriteLine($"An error occurred: {ex.Message}");
}




