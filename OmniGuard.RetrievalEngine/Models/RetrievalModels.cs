namespace OmniGuard.RetrievalEngine.Models;

public record ComplianceQueryRequest(string UserPrompt);

public class SearchMatchDto
{
    // 1. Mandatory parameterless constructor for Dapper reflection materialization
    public SearchMatchDto()
    {
    }

    public string VectorId { get; set; } = string.Empty;

    // 2. Kept as byte[] to perfectly match SQL Server's BINARY(32) type
    public byte[] RowHash { get; set; } = Array.Empty<byte>();

    public int Rank { get; set; }

    public string ParentBlobUrl { get; set; } = string.Empty;
}

public class RrfScoreTracker
{
    public string DeterministicId { get; set; } = string.Empty;
    public byte[] RowHash { get; set; } = Array.Empty<byte>();
    public string ParentBlobUrl { get; set; } = string.Empty; // Track URL extracted directly from metadata
    public float DenseScore { get; set; } = 0f;
    public float SparseScore { get; set; } = 0f;
    public float CombinedRrfScore => DenseScore + SparseScore;
}

public record PineconeQueryRequest(string Namespace, float[] Vector, int TopK, bool IncludeMetadata = true);

// Pinecone Metadata Payload Map Contracts
public record PineconeMetadata(string DocumentId, int PageNumber, int ChunkIndex, string ParentBlobUrl);
public record PineconeMatch(string Id, double Score, PineconeMetadata? Metadata); // Captures stored metadata values
public record PineconeQueryResponse(List<PineconeMatch> Matches);

public record ComplianceApiResponse(
    string Status,
    string SanitizedQuery,
    string ResearcherSummary,
    string AuditorVerdict
);