

using RAGAndPineConeVectorDataBase;

ChunckingAndEmbedding chunckingAndEmbedding = new ChunckingAndEmbedding();
await chunckingAndEmbedding.GenerateAndSaveEmbedding();// Note this s=genrating and storing the embedding is one time activity to store the vecotr. Ideally should be a separate module to run and store the vecotr.
// then simply use that vector store to fetch the context and augment that context to LLM promt 
Console.WriteLine(chunckingAndEmbedding.GetType().Name + " has completed generating and saving embeddings to PineCone Vector Database.");

//search the vector store with a query and get the result. Note this is a step for augmentation, you can use the retrieved result as a context to generate answer for the user query.
Console.WriteLine("Please enter your query: ");
//string context = await chunckingAndEmbedding.GetSearhResultFromPineConeVectorStore(Console.ReadLine());
string response= await chunckingAndEmbedding.GetResponseFromLLMAfterAugmentation(Console.ReadLine());
Console.WriteLine(response);
Console.ReadLine();
