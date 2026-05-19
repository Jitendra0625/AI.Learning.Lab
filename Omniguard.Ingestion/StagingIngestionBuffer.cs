using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public enum ProcessingStatus : byte
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3
    }

    public class StagingIngestionBuffer
    {
        public long SequenceId { get; set; }
        public string DocumentId { get; set; } = null!;
        public int PageNumber { get; set; }
        public int ChunkIndex { get; set; }
        public string ExtractedText { get; set; } = null!;
        public byte[] RowHash { get; set; } = null!; // 32-Byte SHA-256
        public string VectorId { get; private set; } = null!; // Read-only (Database Persisted Column)
        public string ParentBlobUrl { get; set; } = null!;
        public ProcessingStatus Status { get; set; } = ProcessingStatus.Pending;
        public bool VectorSynced { get; set; }
        public int RetryCount { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LockedUntil { get; set; }
        public byte[] RowVersion { get; set; } = null!;
    }
}
