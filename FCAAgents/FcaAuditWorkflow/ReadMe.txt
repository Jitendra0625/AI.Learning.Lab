# 🕵️‍♂️ Multi-Agent Compliance Auditor (Azure AI Foundry + AI Search)

## 🧠 Project Overview
This is a **C# .NET 9.0** console application that demonstrates a modern, multi-agent AI workflow. It acts as an automated regulatory compliance auditor using a Retrieval-Augmented Generation (RAG) pipeline. 

The application takes a user's query, routes it, performs a hybrid semantic search against the Financial Conduct Authority (FCA) rulebook, and uses an autonomous agent to audit the findings for strict compliance.

### 🛠️ Tech Stack
* **Framework:** .NET 9.0 (C#)
* **SDKs:** `Microsoft.Extensions.AI`, `Microsoft.Agents.AI.Workflows`
* **LLM Engine:** Azure AI Foundry (OpenAI)
* **Knowledge Base:** Azure AI Search
* **Observability:** OpenTelemetry & Azure Application Insights

---

## 🏗️ 1. Azure AI Foundry (LLM Deployment)
This project uses Azure AI Foundry to power the agents.

### Setup Instructions
1. Navigate to your **Azure AI Foundry** project.
2. Deploy a foundation model (e.g., `gpt-4o` or `gpt-4`).
3. **Important:** Name the deployment **`chatmodel`**. If you name it something else, you must update the `GetChatClient("chatmodel")` string in `Program.cs`.
4. Retrieve your **Endpoint** and **API Key** and place them in the `openAiEndpoint` and `openAiKey` variables. (Using `ApiKeyCredential` bypasses local Entra ID friction for development).

---

## 🔍 2. Azure AI Search (The RAG Knowledge Base)
The application uses Azure AI Search as its factual grounding layer. 

### Index Schema Requirements
To support the `SearchFcaRulesAsync` tool, your index (named `"indexname"` in the code) must be configured with specific fields:
* **`chunk` (Edm.String):** This holds the actual readable text of the FCA rule.
* **`vector` (Collection(Edm.Single)):** This holds the embeddings.

### The Retrieval Layer (Hybrid Search + RRF)
The code utilizes a state-of-the-art Hybrid Search approach:
* **Auto-Vectorization:** By using `VectorizableTextQuery(query)`, we don't need to manually embed the user's text before searching. Azure AI Search automatically vectorizes the incoming query at the endpoint.
* **Reciprocal Rank Fusion (RRF):** It simultaneously performs a keyword search and a vector search (`KNearestNeighborsCount = 3`), fusing the results to ensure the most mathematically and contextually relevant clauses are returned.
* The results are concatenated and handed back to the agent as a string.

---

## 🤖 3. Multi-Agent Graph Architecture
Instead of a linear script, this app uses `Microsoft.Agents.AI.Workflows` to build a state-sharing graph. The shared state is an `IReadOnlyList<ChatMessage>`.

### The Agents
1. **RouterAgent:** The gatekeeper. Determines if the prompt requires a knowledge base lookup or a direct response.
2. **MyFCAAgent:** The specialist. It is equipped with the `fcaSearchTool` to query Azure AI Search.
3. **AuditAgent:** The judge. Evaluates the retrieved FCA data against the user's scenario.

### Conditional Routing
Once the `AuditAgent` completes its evaluation, a conditional edge inspects the final string in the state:
* If it explicitly contains `"Orchestrated Verdict: AUDIT PASSED"`, it routes to the **NotifyUser** node.
* If it does NOT, it routes to the **HumanEscalation** node to flag a compliance violation.

---

## 📊 4. Observability (OpenTelemetry)
Enterprise-grade observability is built-in. Telemetry captures token usage, agent execution times, and graph routing paths.

### Setup & Implementation
1. **Application Insights:** An Application Insights resource is usually auto-created with your Foundry project. Grab the **Connection String** and place it in the `appInsightsConnectionString` variable.
2. **The Interceptor:** The standard `chatClient` is wrapped using `.AsBuilder().UseOpenTelemetry()` to create `chatClient1`. 
3. **Important:** `chatClient1` MUST be the client passed to the agents so that their inner LLM calls are tracked.
4. **Where to view data:** Because this is a short-lived console app, metrics won't appear in "Live Metrics". To see your token costs and waterfall traces, go to **Transaction Search** in the Azure Portal.

---

## 🚀 How to Run Locally

1. Clone this repository.
2. Open `Program.cs` and replace the placeholder keys and endpoints:
   * `foundry openai url` / `foundry key`
   * `ai search key` / `https://<AISearch>.search.windows.net`
   * `InstrumentationKey=MyKey...`
3. Open your terminal in the project directory.
4. Run the following command:
   ```bash
   dotnet run