using Dapper;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Pinecone;
using Microsoft.SemanticKernel.Text;
using OmniGuard.Compliance.Engine.Models;

// We need the NATIVE client for the VectorStore constructor
using NativePineconeClient = Pinecone.PineconeClient;

namespace OmniGuard.Compliance.Engine.Services
{
    /// <summary>
    /// This class will ingest vectors and keyword to allow hybrid search . parallel search that combines Semantic (Vector) Search and Lexical (Keyword) Search into a single ranked list
    /// </summary>
    internal class HybridIngestionService
    {
         //"""
         //   Vector Search (Dense): This uses your BGE-small embeddings to find chunks with similar meanings, even if the exact words are different.
         //   Keyword Search (Lexical): This uses the text you marked with IsFullTextIndexed = true to find exact word matches, such as specific MCOB section codes or banking terminology
         //   """;

           
        private readonly IEmbeddingGenerator<string,Embedding<float>> _embeddingGenerator;
        private readonly PineconeVectorStore _pineconeVectorStore;
        private readonly SQLLiteService _sqlLiteDB;
        private readonly NativePineconeClient _client;
        public HybridIngestionService(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, NativePineconeClient client, SQLLiteService sqlLiteDB)
        {
            _embeddingGenerator = embeddingGenerator;
            _pineconeVectorStore = new PineconeVectorStore(client);
            _client = client;
            _sqlLiteDB = sqlLiteDB;
        }

        public async Task GenerateHybridVecors()
        {
           
            string pdfPath = Path.Combine(AppContext.BaseDirectory, @"Data\FCA_MCOB.pdf");
            var pdfReader= new PdfReader(pdfPath);
            var pdfDoc= new PdfDocument(pdfReader);


            // as we will be creating the chunk text inSQL lite db for keyword search. Let we create the local Db here
            await _sqlLiteDB.PrepareLexicalLayer();

            // Proceed with chunking and storing the childs vectors in pinecone and chunked text in sql lite for hybrid search
            for (int i = 1; i <= Math.Min(200,pdfDoc.GetNumberOfPages()); i++)
            {
                var pageText=PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i));

                string parentId = $"doc-fcahybrid-page-{i}"; // Dynamic Parent ID
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

                var collection = _pineconeVectorStore.GetCollection<string, HybridComplianceRecord>("retail-bank-regulatory-hybridindex");

                // 2. Save the full page text to a FILE instead of Pinecone Metadata
                string parentFileName = $"{parentId}.txt";
                await File.WriteAllTextAsync(Path.Combine(parentStoragePath, parentFileName), pageText);


                var parentVector = await _embeddingGenerator.GenerateAsync(new[] { $"Parent anchor for page {i}" });
                var record = new HybridComplianceRecord
                {
                    Id = parentId,
                    Text = $"Link to {parentFileName}", // Just a reference
                    ChunkType = "parent",
                    PageNumber = i,
                    Vector = parentVector[0].Vector
                };
                await collection.UpsertAsync(record); // Upsert Parent record. As I am using the hybrid storage pinecone and oarent files in localmachine. I may  not need to upsert parent as parent id in child record can five me the file name to fetch from local
                                                      // folder.but I am stoing to showcase example if we are updatiung parent /full text also in pinecone

#pragma warning disable SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                var lines = TextChunker.SplitPlainTextLines(pageText, 128);
                var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, 512);

#pragma warning restore SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                var childRecords = new List<HybridComplianceRecord>();
                var childVector = await _embeddingGenerator.GenerateAsync(paragraphs);
                for (int j= 0; j<paragraphs.Count();j++)
                {
                    childRecords.Add(new HybridComplianceRecord
                    {
                        Id=$"{parentId}-child-{j}",
                        Text = paragraphs[j],
                        ChunkType = "child",
                        Parent_Id   = parentId,
                        PageNumber=i, 
                        Vector = childVector[j].Vector

                    });
                }
               
                // Instead of passing the whole list at once
                // We loop through the list and upsert each record
                foreach (var child in childRecords)
                {
                    await collection.UpsertAsync(child);
                }
                await _sqlLiteDB.IngestionInSQLLite(childRecords);
                Console.WriteLine($" Page {i}: 1 Parent and {childRecords.Count} Children stored in pinecone vector store and in SQL DB.");
            }
        }

       
    }
}
