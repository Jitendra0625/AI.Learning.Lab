using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using OmniGuard.Compliance.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using iText;
using iText.Kernel.Pdf;
//using Pinecone;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.SemanticKernel.Text;
using Microsoft.SemanticKernel.Data;
using Microsoft.Extensions.VectorData;
using iText.Signatures;
using PineconeClient = Microsoft.SemanticKernel.Connectors.Pinecone;
// We need the NATIVE client for the VectorStore constructor
using NativePineconeClient = Pinecone.PineconeClient;
using iText.Layout.Element;
using Microsoft.SemanticKernel.Embeddings;
using static System.Net.Mime.MediaTypeNames;
using Microsoft.SemanticKernel.ChatCompletion;
namespace OmniGuard.Compliance.Engine.Services
{
    /// <summary>
    /// This class will be chunking the pdf, embedding and stroing the vectors with meta data in Pinecone
    /// </summary>
    internal class IngestionService
    {
        private readonly PineconeVectorStore _vectorStore;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
       
        public IngestionService(NativePineconeClient client, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
        {
            _vectorStore = new PineconeVectorStore(client);
            _embeddingGenerator = embeddingGenerator;
        }

        public async Task IndexLargePolicyAsync()
        {
            var collection = _vectorStore.GetCollection<string, ComplianceRecord>("retail-bank-regulatory-index");
            using var reader = new PdfReader(Path.Combine(AppContext.BaseDirectory, @"Data\FCA_MCOB.pdf"));
            using var pdfDoc = new PdfDocument(reader);
            // This satisfies Pinecone's requirement that every record has a vector.
            var zeroVector = new float[384];


            //for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
            for (int i = 1; i <= Math.Min(200, pdfDoc.GetNumberOfPages()); i++)
            {
                string pageText = PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i));
                string parentId = $"doc-fca-page-{i}"; // Dynamic Parent ID

                // --- STEP 1: Store the PARENT (Full Page Text) ---
                // We don't necessarily need a vector for the parent, just the data

                // I have faced an isue here when storing large parent text due to 
                // Pinecone Serverless has a strict rule: You cannot store large amounts of text in Metadata.
                /*Each Pinecone record has a metadata limit of ~40KB.
                my Child chunks are small (512–800 chars), so they work fine.
                my Parent record is a full page (often 2,000–5,000+ characters).*/

                // Alternate solution, store the large parent text in sql or azure blob or local folder and in pinecone store only a link of parent to get information
                //Using here the local folder
                // 1. Create a local folder for Parents if it doesn't exist
                string parentStoragePath = Path.Combine(AppContext.BaseDirectory, "ParentStore");
                Directory.CreateDirectory(parentStoragePath);

                // 2. Save the full page text to a FILE instead of Pinecone Metadata
                string parentFileName = $"{parentId}.txt";
                await File.WriteAllTextAsync(Path.Combine(parentStoragePath, parentFileName), pageText);

                // Generate a dense vector (BGE 384 dims) to satisfy Pinecone schema
                var parentEmbed = await _embeddingGenerator.GenerateAsync(new[] { $"Parent anchor for page {i}" });
                // 3. Upsert a "Lightweight" Parent record to Pinecone (No large Text)
                var parent = new ComplianceRecord
                {
                    Id = parentId,
                    Text = $"Link to {parentFileName}", // Just a reference
                    ChunkType = "parent",
                    PageNumber = i,
                    Vector = parentEmbed[0].Vector
                };
               await collection.UpsertAsync(parent);

                // --- STEP 2: Create and Store CHILDREN (Searchable Chunks) ---
#pragma warning disable SKEXP0050
                var lines = TextChunker.SplitPlainTextLines(pageText, 128);
                var pageChunks = TextChunker.SplitPlainTextParagraphs(lines, 512);
#pragma warning restore SKEXP0050

                // Need vectors for chunks as this search will be semantic and then will use the parent id to get full text
                var embeddings = await _embeddingGenerator.GenerateAsync(pageChunks);
                var childRecords = new List<ComplianceRecord>();

                for (int j = 0; j < pageChunks.Count; j++)
                {
                    childRecords.Add(new ComplianceRecord
                    {
                        Id = $"{parentId}-child-{j}",
                        Text = pageChunks[j], // Small snippet
                        Vector = embeddings[j].Vector, // For searching
                        Parent_Id = parentId, // THE LINK
                        ChunkType = "child",
                        PageNumber = i
                    });
                }

                // 4. Execute Batch
                //await collection.UpsertAsync(childRecords); // changed this to below as using older version of nuget packge

                // Instead of passing the whole list at once
                // We loop through the list and upsert each record
                foreach (var child in childRecords)
                {
                    await collection.UpsertAsync(child);
                }

                Console.WriteLine($"✅ Page {i}: 1 Parent and {childRecords.Count} Children stored.");
            }
        }

    }
}
