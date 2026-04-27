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











\--------------------------------------------------------------------------------------------------------------------------------------------------------



**Note:** OmniGuard is an independent research project developed in my personal architectural lab. It is designed as a Proof of Concept (PoC) for enterprise-grade AI governance and is not affiliated with any commercial entity.



**OmniGuard: Enterprise Compliance RAG Engine**

Architecting Reliable AI for Retail Banking

OmniGuard is a high-precision Retrieval-Augmented Generation (RAG) engine built to process large-scale regulatory documents (like the 550-page FCA MCOB Handbook). Unlike "basic" RAG demos, OmniGuard is engineered for authority, privacy, and architectural governance.

**🏗️ The Architecture**

**1. Parent-Document Retrieval (PDR)**

**S**tandard RAG often suffers from "Context Loss" because small text chunks lose the surrounding legal meaning.

The Strategy: I implemented a hierarchical "Parent-Child" link.

Execution: We search using granular Child vectors for high-precision matching but retrieve the full Parent page text to ensure the LLM has 100% authoritative context.

**2. Hybrid Storage Pattern**

To bypass the 40KB metadata limits of vector databases (like Pinecone) and enhance security:

Vector Store: Pinecone (Serverless) stores mathematical "Child" embeddings.

Document Store: A secure local file system stores the full "Parent" authoritative text.

The Link: A shared ParentId bridges the cloud math with the local truth.

**3. Multi-Model Validation (The "Judge" Pattern)**

To eliminate hallucinations in a banking environment:

The Logic: Every retrieval is audited by a secondary Hugging Face LLM (The Judge).

The Output: The Judge assigns a Confidence Score (High/Medium/Low). If evidence is partial, the system triggers a Compliance Advisory rather than a definitive answer.

**4. Agentic Governance (.agent.md) and Multi-Agent Governance (Researcher/Auditor)**

The repository is Agent-Ready. I have codified senior architectural standards into a .agent.md file. This ensures that any AI assistant (like GitHub Copilot) enforces our specific PDR and safety patterns during the development lifecycle.

**🛠️ Tech Stack**

Runtime: .NET 9

Orchestration: Semantic Kernel (1.74.0-preview)

Vector Database: Pinecone (Serverless)

Embedding Model: Local BGE-Small-v1.5 (via ONNX) for data privacy.

Validation Model: Hugging Face (Llama-3 / Mistral)

PDF Engine: iText9 (Streaming ingestion for 500+ page documents)

**🚀 Getting Started**

Ingestion

The engine uses a Streaming Pipeline to prevent memory overflow.

Place your PDF in /Data.

Run the Ingestion Service to generate local Parent files and Cloud vectors.

Retrieval

**csharp**

// Search with automatic Judge validation

var result = await retrievalService.GetJudgedContextAsync(query, indexName);

Console.WriteLine(result.Confidence);

Use code with caution.

**🛡️ Why this is "Production-Ready"**

Data Sovereignty: Embeddings are generated locally; full text never leaves the secure store.

Auditability: Every query and Judge reasoning is logged to AuditLog.txt.

Resiliency: Implemented a local keyword fallback if the vector store is unreachable.



\## 🌊 **Agentic Governance \& Multi-Model Audit**

OmniGuard has evolved from a linear RAG pipeline into a multi-agent ecosystem. By moving to an \*\*Agentic Workflow\*\*, we ensure that no response is delivered without independent verification.



\### The Multi-Agent Workforce

We have implemented a specialized "Research \& Audit" loop using \*\*Semantic Kernel Agents\*\*:



1\.  \*\*The Researcher Agent (Lead)\*\*

&#x20;   \*   \*\*Role:\*\* Performs semantic search across Pinecone and local parent stores.

&#x20;   \*   \*\*Capability:\*\* Deconstructs complex user queries into sub-tasks and synthesizes information from multiple MCOB chapters.

2\.  \*\*The Auditor Agent (The Critic)\*\*

&#x20;   \*   \*\*Role:\*\* Acts as a compliance firewall.

&#x20;   \*   \*\*Logic:\*\* Executes a "Chain-of-Verification." It compares the Researcher’s output against the \*raw\* parent text to detect hallucinations or omissions.

3\.  \*\*The Governance Lead (.agent.md)\*\*

&#x20;   \*   \*\*Role:\*\* Enforces architectural standards (PDR patterns, privacy rules) during the coding process.



\### Workflow: The "Self-Correcting" Loop

\- \*\*Step 1:\*\* User asks a vague regulatory question.

\- \*\*Step 2:\*\* Researcher pulls Child-Parent context.

\- \*\*Step 3:\*\* Auditor verifies context vs. answer.





\## The Evolution of OmniGuard





| Wave | Milestone | Focus | Key Tech |

| :--- | :--- | :--- | :--- |

| \*\*W1\*\* | \*\*The Core\*\* | Hybrid Storage \& .NET 9 Setup | Pinecone, BGE-Small |

| \*\*W2\*\* | \*\*Precision\*\* | Parent-Document Retrieval (PDR) | iText9, Hierarchical Indexing |

| \*\*W3\*\* | \*\*Architecture\*\* | Agent-Ready Codebase | .agent.md, Semantic Kernel |

| \*\*W4\*\* | \*\*Governance\*\* | Multi-Agent Audit \& Validation | Researcher/Auditor Agent Loop

| \*\*W5\*\* | \*\*Validation\*\* | Golden Dataset Benchmarking | FCA MCOB Ground Truth, C# CLI Eval |

