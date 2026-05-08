using iText.Kernel.Pdf;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.ServiceBus;
using Microsoft.Extensions.Logging;
using OmniGuard.Compliance.Engine.Services;
using System.IO;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public class PolicyIngestion
    {
        private readonly HybridIngestionService _ingestionService;
        private readonly ILogger<PolicyIngestion> _logger;

        //Dependency Injection via Constructor
        public PolicyIngestion(HybridIngestionService ingestionService, ILoggerFactory loggerFactory)
        {
            _ingestionService = ingestionService;
            _logger = loggerFactory.CreateLogger<PolicyIngestion>();
        }

        //Dependency Injection via Constructor
        //public PolicyIngestion(ILoggerFactory loggerFactory)
        //{
        //    //_ingestionService = ingestionService;
        //    _logger = loggerFactory.CreateLogger<PolicyIngestion>();
        //}

        #region Azure Functions
        // 1. THE WATCHER: Detects file in Blob Storage and notifies Service Bus
        [Function("PolicyInboxWatcher")]
        [ServiceBusOutput("policy-processing-queue", Connection = "ServiceBusConnection")]
        public ProcessingMessage Watcher(
            [BlobTrigger("policy-index/{name}", Connection = "AzuriteConnectionString")] string myBlob,
            string name)
        {
            _logger.LogInformation($"[Watcher] PDF detected: {name}. Queuing for processing...");
            return new ProcessingMessage { BlobName = name }; // This return value goes straight to the Service Bus queue
        }

        // 2. THE PROCESSOR: Triggered by Service Bus to do the heavy PDF lifting
        [Function("PolicyProcessor")]
        // 5 retries, starting with 10s delay, maxing at 1m
        //[ExponentialBackoffRetry(5, "00:00:10", "00:01:00")]  attribute will not work with your Service Bus trigger in the Isolated Worker model.
        public async Task Processor(
            [ServiceBusTrigger("policy-processing-queue", Connection = "ServiceBusConnection")] ProcessingMessage message,
            [BlobInput("policy-index/{BlobName}", Connection = "AzuriteConnectionString")] byte[] pdfContent)
        {
            try
            {

                _logger.LogInformation($"[Processor] Starting extraction for: {message.BlobName}");
                using(var reader= new PdfReader(new MemoryStream(pdfContent)))
                using (var pdfDoc = new PdfDocument(reader))
                {
                    
                        // Logic to extract and send to Pinecone/SQLite via your Service
                        await _ingestionService.GenerateHybridVecors(pdfDoc);
                    
                }
                _logger.LogInformation($"[Processor] Successfully indexed: {message.BlobName}");
                //if (message.BlobName == "2.pdf") // for 2nd pdf try 5 times and move it to DQL and continue on 3.pdf
                //{
                //    throw new Exception("Reproduce the error secnario to allow Azure fucntion to try 5 times and then the message will move the DQL in service bus");
                //}
            }
            catch (Exception ex)
            {
                throw;// Need to throw the exception for service bus to ahndle this error and retry. If exception handled here, it will be success for service bus even the actual processing not happeend.
            }

            
        }
        #endregion
    }
    public class ProcessingMessage
    {
        public string BlobName { get; set; }
    }
}

