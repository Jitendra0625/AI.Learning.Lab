# 🏦 Financial Statement Alert Workflow (Multi-Agent AI)

## 🧠 Project Overview
This project is a **C# .NET 9.0 console application** that demonstrates an autonomous financial risk-monitoring pipeline. It utilizes a multi-agent graph architecture to securely ingest bank statements, parse unstructured financial data, evaluate balances against custom risk thresholds, and dynamically draft personalized alerts for human-in-the-loop approval.

Built using the preview SDKs: **`Microsoft.Extensions.AI`** and **`Microsoft.Agents.AI.Workflows`**.

---

## 🏗️ Architecture & Tech Stack
* **Language & Runtime:** C# / .NET 9.0
* **LLM Engine:** Azure AI Foundry (`chatmodel` deployment).
* **Document Parsing:** Azure AI Document Intelligence (`prebuilt-layout` model).
* **Secure File Storage:** Azure Blob Storage (`bank-statements` container).
* **Agent Framework:** Microsoft.Agents.AI.Workflows (Graph-based orchestration).
* **Observability:** OpenTelemetry + Azure Application Insights.

---

## 📄 The Ingestion Layer (Blob Storage + OCR)
Instead of forcing the LLM to read raw files, this application uses a dedicated C# tool (`DownloadAndParsePdfAsync`) to handle document extraction securely:
1. **Blob Retrieval:** Connects to Azure Blob Storage to download the specified PDF statement directly into memory without relying on local file paths or complex SAS tokens.
2. **Structural OCR:** Streams the binary data to Azure AI Document Intelligence. It uses the `prebuilt-layout` model to extract not just text, but complex tabular data (like transaction histories) with high accuracy.
3. **Agent Handoff:** The parsed text is handed back to the agent framework for evaluation.

---

## 🤖 The Multi-Agent Workflow Graph
The workflow operates sequentially, passing an `IReadOnlyList<ChatMessage>` alongside strongly-typed business rules (e.g., the £100 threshold).

1. **StatementParserAgent:** The extractor. Uses the Blob/OCR tool to read the document and pull out the "Available Balance" and line-item transactions.
2. **BalanceEvaluationAgent:** The logic engine. Evaluates if the extracted balance is below the £100 threshold and pinpoints the high-value transactions responsible for the drop. Assigns a status tag.
3. **DraftingAgent:** The communications hub. Drafts a highly personalized alert if triggered, or dismisses the workflow if the balance is safe.
4. **Action Nodes (Conditionals):** Standard C# `Action` delegates (`DispatchAlert` and `LogAndDismiss`) execute terminal logic based on the drafted outcome.

### 🔀 Conditional Routing
The edges rely on checking the `DraftingAgent`'s final output tags:
```csharp
// Trigger Alert Condition
condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: SEND_ALERT]") == true

// Safe Dismissal Condition
condition: msgs => msgs?.LastOrDefault()?.Text?.Contains("[Action: DISMISS_LOG]") == true