using InventoryAgent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;


string ? modelId = Environment.GetEnvironmentVariable("HuggingFaceModelId", EnvironmentVariableTarget.User);; // Or any Chat-optimized model
string? apiKey = Environment.GetEnvironmentVariable("HuggingFaceAPIKey", EnvironmentVariableTarget.User);
var builder = Kernel.CreateBuilder();

var systemMessage = """
    You are a strict Inventory Assistant.
    
    RULES:
    1. ONLY answer questions based on the products found in the provided tools.
    2. If a user asks for a product NOT in the database, politely say you don't have information on that.
    3. If a user asks about VAT (Value Added Tax), ALWAYS reply: "All our prices are all-inclusive of VAT."
    4. Do not answer general knowledge questions (e.g., "Who is the president?" or "How do I cook pasta?"). 
       Reply: "I am only authorized to assist with inventory and product pricing."
    """; 
builder.AddOpenAIChatCompletion(
    modelId: modelId?? "",
    apiKey: apiKey,
    endpoint: new Uri("https://router.huggingface.co/v1")
    );

// We can add multiple plugins to the kernel and the AI will be able to use all of them in the conversation. This allows us to create a more complex agent that can handle a wider range of tasks by leveraging different plugins for different functionalities.
builder.Plugins.AddFromType<InventoryPlugin>(); // Registering the plugin to kernel so that it can be used in the conversation. This is very important step without this the function in plugin will not be invoked.
builder.Plugins.AddFromType<CalculatorPlugin>(); // Registering the plugin to kernel so that it can be used in the conversation. This is very important step without this the function in plugin will not be invoked.
Kernel kernel = builder.Build();
var chatService = kernel.GetRequiredService<IChatCompletionService>();
var history = new ChatHistory(systemMessage);

Console.WriteLine("--- Semantic Kernel Chat Started (Type 'exit' to quit) ---");
var settings = new OpenAIPromptExecutionSettings
{
    // 2. Enable Auto Function Calling in the settings
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions, // This will invoke the plugin fucntion GetProductDetails
    MaxTokens = 500,
    Temperature = 0.1F,
    TopP = 0.9F
};
while (true)
{
    Console.Write("User: ");
    string? userInput = Console.ReadLine();
    if (userInput == null || userInput.Trim().ToLower() == "exit")
    {
        Console.WriteLine("Exiting chat...");
        break;
    }
    else
    {
        history.AddUserMessage(userInput);
        var respose = await chatService.GetChatMessageContentAsync(
            history,
            executionSettings: settings,
            kernel: kernel);
        Console.WriteLine($"AI: {respose.Content}");
        history.AddAssistantMessage(respose.Content ?? "");
    }
}