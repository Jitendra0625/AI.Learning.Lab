# AI.Learning.Lab

Agentic AI Using Semantic Kernel

OmniRAG.core is the project where I am having different RAG pipleines mentioned in this 



What I have Built \& Learned

1\. Structured Data Engineering

Semantic Manual Chunking: Moved beyond basic character-count splitting. Implemented a Regex-based ingestion pipeline that respects document headers (\[Category: X | Year: Y]).

Metadata Enrichment: Engineered a vector schema in Pinecone that stores structured attributes (Category, Year) alongside unstructured text.

2\. Local-First Embedding Strategy

Local Inference: Implemented the Microsoft.Extensions.AI abstractions to run BERT/ONNX models locally, reducing latency and cost compared to API-based embeddings.

Vector Dimensionality: Managed 384-dimension vector spaces for high-efficiency semantic mapping.

3\. Precision Retrieval (Pre-Filtering)

Metadata Filtering: Solved the "Temporal Confusion" problem in RAG by implementing server-side pre-filtering in Pinecone.

SDK Mastery: Developed direct integration with the Pinecone .NET SDK (3.x) to handle complex Metadata dictionary filters and $eq operators.

4\. Autonomous Intent Extraction (The Driver)

Query Transformation: Built a "Pre-Search" reasoning layer where an LLM analyzes the user's natural language to extract search parameters.

JSON-Schema Enforcement: Trained the LLM to output structured search intents, allowing the system to bridge the gap between "human talk" and "database filters."



🛠️ The Tech Stack

Runtime: .NET 8/9

Orchestration: Semantic Kernel / Microsoft.Extensions.AI

Vector DB: Pinecone (Serverless)

Embeddings: ONNX / Local BERT

LLM: GPT-4o / LLM from Hugging Face)



🔜 What's Next? (The Roadmap)

To evolve this into a production-grade system, I am targeting the following "Senior AI Engineer" milestones:

📍 Parent-Document Retrieval (Small-to-Big)

Goal: Index small sentences for high-precision matching, but retrieve the full original paragraph (the "Parent") to provide the LLM with better context.

Why: Prevents "context fragmentation" where the AI only sees a tiny piece of the story.

📍 Reranking (Cross-Encoders)

Goal: Retrieve the Top 10 results from Pinecone and use a specialized Reranker model to select the absolute best 3.

Why: Vector search is good at "similarity," but Reranking is better at "relevance."

📍 Conversational Memory

Goal: Implement a "Buffer Window" to remember the last 5 user interactions.

Why: Allows the user to ask follow-up questions like "Tell me more about that 2023 policy" without repeating the year.

💡 Project Reflection

The biggest challenge overcome was handling Dependency Injection and Namespace conflicts between experimental Semantic Kernel packages and the official Pinecone SDK. This project proves that a hybrid approach—using SDKs for data and Frameworks for reasoning—is the most stable path for AI applications.

