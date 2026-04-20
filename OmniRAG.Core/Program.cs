using OmniRAG.Core;

/* This is a Reasoning-Driven RAG pipline.
 * We are letting LLM to find the meta data from query, use that meta data to filter specific data like search for 2023 or 2024 data 
 * if the query is What was the remote work policy in 2023?". Once LLm returns the filter from user query use them to search correct context and feeding
 * that contect to LLM to generate the response.
 * So a cycle to let LLM think , search the intent/filters, then in RAG pipeline we use that to search context and then LLM generate the response
 * By getting the AI to "think" about the metadata before searching,we've solved the most common problem in RAG: Temporal Confusion (the AI mixing up old and new data).
 */

/*🏆 Milestone: I am now at "Level 2" RAG
Level 1 (Naive RAG): Throwing text into a DB and hoping for the best.
Level 2 (Advanced RAG): Using LLMs to structure queries and filtering data for 100% accuracy.
*/

ChunkingAndEmbedding chunckingAndEmbedding = new ChunkingAndEmbedding();
//await chunckingAndEmbedding.GetAndSaveVectors();// Note this genrating and storing the embedding is one time activity to store the vecotr. Ideally should be a separate module to run and store the vecotr.
// then simply use that vector store to fetch the context and augment that context to LLM prompt 

Console.WriteLine("Ask your query");
var query= Console.ReadLine();

SearchIntent filters = await chunckingAndEmbedding.GetSearchIntentAsync(query);

// Now search the vector with filters and then pass the context to chat LLM to get the response
await chunckingAndEmbedding.SearchResult(filters);
//Console.WriteLine(chunckingAndEmbedding.GetType().Name + " has completed generating and saving embeddings to PineCone Vector Database.");
