using Azure.Storage.Blobs;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Text;
using Omniguard.Ingestion;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public class PolicyIngestion
    {
        private readonly HybridIngestionsService _ingestionService;
        private readonly ILogger<PolicyIngestion> _logger;
        private readonly AuditDbContext _context;
        private readonly AuditService _auditService;
        private readonly IConfiguration _configuration;
        private readonly IStagingBufferRepository _stagingRepository;

        // Dependency Injection via Constructor
        public PolicyIngestion(
            HybridIngestionsService ingestionService,
            ILoggerFactory loggerFactory,
            AuditDbContext context,
            AuditService auditService,
            IConfiguration configuration, IStagingBufferRepository stagingRepository)
        {
            _ingestionService = ingestionService;
            _logger = loggerFactory.CreateLogger<PolicyIngestion>();
            _context = context;
            _auditService = auditService;
            _configuration = configuration;
            _stagingRepository = stagingRepository;
        }

        #region Azure Functions
        // The Watcher: Detects file in Blob Storage, split pdf in paages, store in policy-parentpages blob container and notifies Service Bus
        [Function("OmniGuardWatcherEngine")]
        [ServiceBusOutput("policy-processing-queue", Connection = "ServiceBusConnection")]
        public async Task<List<ProcessingMessage>> Watcher(
        // By binding to Uri instead of Stream, the trigger fires without locking the file
        [BlobTrigger("policy-index/{name}", Connection = "AzureWebJobsStorage")] string blobUri,
        string name)
            {
                _logger.LogInformation("[Watcher] PDF detected via URI: {Name}. Slicing text...", name);

                var pageMessages = new List<ProcessingMessage>();
                string cleanFileName = Path.GetFileNameWithoutExtension(name).Replace(" ", "_").ToLowerInvariant();
                string documentId = $"{cleanFileName}_{Guid.NewGuid().ToString()[..8]}";

                string connectionString = _configuration["AzureWebJobsStorage"];
                var blobServiceClient = new BlobServiceClient(connectionString);

                // Explicitly target both source and chunk containers
                var sourceContainer = blobServiceClient.GetBlobContainerClient("policy-index");
                var targetContainer = blobServiceClient.GetBlobContainerClient("policy-parent-pages");
                await targetContainer.CreateIfNotExistsAsync();

                // 💡 Manually download the stream to bypass the host lease manager 404 trap
                var sourceBlobClient = sourceContainer.GetBlobClient(name);
                using var pdfStream = new MemoryStream();
                await sourceBlobClient.DownloadToAsync(pdfStream);
                pdfStream.Position = 0;

                using (var reader = new PdfReader(pdfStream))
                using (var srcPdfDoc = new PdfDocument(reader))
                {
                    int totalPages = srcPdfDoc.GetNumberOfPages();
                    _logger.LogInformation("[Watcher] DocumentId: {DocId} contains {Count} pages.", documentId, totalPages);

                    for (int i = 1; i <= totalPages; i++)
                    {
                        string chunkBlobName = $"{documentId}_page_{i}.pdf";
                        var chunkClient = targetContainer.GetBlobClient(chunkBlobName);

                        var dstMemoryStream = new MemoryStream();
                        var writer = new PdfWriter(dstMemoryStream);
                        writer.SetCloseStream(false);

                        using (var dstPdfDoc = new PdfDocument(writer))
                        {
                            srcPdfDoc.CopyPagesTo(i, i, dstPdfDoc);
                        }

                        dstMemoryStream.Position = 0;
                        using (dstMemoryStream)
                        {
                            await chunkClient.UploadAsync(dstMemoryStream, true);
                        }

                        _logger.LogInformation("[Watcher] Stored split file page {Index} successfully.", i);

                        pageMessages.Add(new ProcessingMessage
                        {
                            DocumentId = documentId,
                            BlobName = chunkBlobName,
                            TargetPage = i,
                            TotalPages = totalPages
                        });
                    }
                }

                return pageMessages;
            }

        // The Processor: Triggered as soon as any message in service bus queue, download the sliced pdf pages from blob and send to hybrid ingestion service for embedding, vector and SQLLite processing
        [Function("OmniGuardProcessorEngine")]
        public async Task Run(
          // 💡 Binds directly to the payload queue property contract emitted by your Watcher Engine
          [ServiceBusTrigger("policy-processing-queue", Connection = "ServiceBusConnection")] ProcessingMessage message,
          CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("[Processor] Received Task -> DocID: {DocId}, Target Page: {PageNum}", message.DocumentId, message.TargetPage);

            try
            {
                // 1. Connect and pull the target slice file from the local Azurite container
                string connectionString = _configuration["AzureWebJobsStorage"];
                var blobServiceClient = new BlobServiceClient(connectionString);
                var containerClient = blobServiceClient.GetBlobContainerClient("policy-parent-pages");
                var blobClient = containerClient.GetBlobClient(message.BlobName);

                string parentBlobUrl = blobClient.Uri.ToString(); // For getting the parent page for full text in retrieval
                string extractedText = string.Empty;

                using (var memoryStream = new MemoryStream())
                {
                    await blobClient.DownloadToAsync(memoryStream, cancellationToken);
                    memoryStream.Position = 0;

                    // 2. Extract text from Page 1 (since the Watcher isolated this page into its own file)
                    using var pdfReader = new PdfReader(memoryStream);
                    using var pdfDocument = new PdfDocument(pdfReader);
                    var page = pdfDocument.GetPage(1);
                    extractedText = PdfTextExtractor.GetTextFromPage(page);
                }

                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    _logger.LogWarning("[Processor] Blank text block detected for DocID: {DocId}, Page: {PageNum}. Skipping database staging.", message.DocumentId, message.TargetPage);
                    return;
                }

                // 3. Segment sentences into clean paragraph chunks using Semantic Kernel
#pragma warning disable SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                var lines = TextChunker.SplitMarkDownLines(extractedText, maxTokensPerLine: 40);

                var paragraphs = TextChunker.SplitMarkdownParagraphs(lines, maxTokensPerParagraph: 250);
#pragma warning restore SKEXP0050 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

                var chunksToUpsert = new List<IngestionChunkPayload>();
                int chunkIndex = 0;

                foreach (var paragraphText in paragraphs)
                {
                    chunksToUpsert.Add(new IngestionChunkPayload(
                        DocumentId: message.DocumentId,
                        PageNumber: message.TargetPage,
                        ChunkIndex: chunkIndex++,
                        ExtractedText: paragraphText,
                        ParentBlobUrl: parentBlobUrl
                    ));
                }

                // 4. Batch upsert records into SQL Server with real-time SHA-256 change detection
                await _stagingRepository.UpsertChunkBatchAsync(chunksToUpsert, cancellationToken);

                stopwatch.Stop();
                _logger.LogInformation("[Processor] Success -> Staged {Count} text chunks for DocID: {DocId}, Page: {PageNum} in {Ms}ms",
                    chunksToUpsert.Count, message.DocumentId, message.TargetPage, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[Processor] Critical processing failure for DocID: {DocId}, Page: {PageNum} after {Ms}ms",
                    message.DocumentId, message.TargetPage, stopwatch.ElapsedMilliseconds);

                // Re-throw to trigger standard Service Bus message retry policies or DLQ allocation
                throw;
            }
        }

       
        //    [Function("OmniGuardProcessorEngine")]
        //    public async Task Processor(
        //[ServiceBusTrigger("policy-processing-queue", Connection = "ServiceBusConnection")] string messageBody)
        //    {
        //        var message = JsonSerializer.Deserialize<ProcessingMessage>(messageBody);
        //        if (message == null) return;

        //        var logData = new IngestionAuditLog
        //        {
        //            // Keep this naming convention so SQL tables record "_Page_2" correctly
        //            BlobName = $"{message.BlobName.Split('_')[0]}_Page_{message.TargetPage}",
        //            CorrelationId = message.CorrelationId,
        //            Status = "Processing"
        //        };

        //        try
        //        {
        //            _logger.LogInformation($"[Processor] [{message.CorrelationId}] Ingesting page {message.TargetPage}/{message.TotalPages} of {message.BlobName}");

        //           // await _auditService.LogStart(logData);

        //            string connectionString = _configuration["AzuriteConnectionString"];
        //            var blobServiceClient = new BlobServiceClient(connectionString);

        //            // FIX: Switch container pointer to point directly to your chunk storage bucket
        //            var containerClient = blobServiceClient.GetBlobContainerClient("policy-chunks");
        //            var blobClient = containerClient.GetBlobClient(message.BlobName);
        //            int childChunks = 0;

        //            using (var memoryStream = new MemoryStream())
        //            {
        //                await blobClient.DownloadToAsync(memoryStream);
        //                memoryStream.Position = 0;

        //                using (var reader = new PdfReader(memoryStream))
        //                using (var pdfDoc = new PdfDocument(reader))
        //                {
        //                    var page = pdfDoc.GetPage(1);
        //                    string pageText = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(page);

        //                    childChunks = await _ingestionService.GenerateHybridVecors(message.TargetPage, pageText);
        //                }
        //            }

        //            logData.Status = $"Success page_{message.TargetPage}_childchunks_{childChunks}";
        //           // await _auditService.LogComplete(logData);
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError($"[Processor] Error on page {message.TargetPage}: {ex.Message}");
        //            logData.Status = "Error";
        //            logData.ErrorDetails = ex.Message;
        //           // await _auditService.LogError(logData);
        //            throw;
        //        }
        //    }

        #endregion
    }

    public class ProcessingMessage
    {
        public string CorrelationId { get; set; }
        public string DocumentId { get; set; }
        public string BlobName { get; set; }
        public int TargetPage { get; set; }
        public int TotalPages { get; set; }
    }
}

