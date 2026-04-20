using Microsoft.Extensions.VectorData;
using OneOf.Types;

namespace OmniRAG.Core
{
    internal class PolicyRecords
    {
        [VectorStoreKey]
        public string Id { get; set; }

        [VectorStoreData]
        public string Text { get; set; }

        [VectorStoreData]
        public string Category { get; set; }

        [VectorStoreData]
        public int Year { get; set; }

        [VectorStoreVector(384)] // model (bge-small uses 384)
        public ReadOnlyMemory<float> Embedding { get; set; }
    }
}
