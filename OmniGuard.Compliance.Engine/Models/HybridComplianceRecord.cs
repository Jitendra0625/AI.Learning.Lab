using Microsoft.Extensions.VectorData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmniGuard.Compliance.Engine.Models
{
    public class HybridComplianceRecord
    {
        [VectorStoreKey]
        public string Id { get; set; }

        // CRITICAL: Set IsFullTextIndexed = true to enable keyword/BM25 search on this field
        [VectorStoreData(IsFullTextIndexed = true)]
        public string Text { get; set; }

        // Dense Vector (BGE-small 384)
        // Note: Hybrid search REQUIRES DotProduct
        [VectorStoreVector(384, DistanceFunction = DistanceFunction.DotProductSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }

        // Sparse Vector for Hybrid/Keyword Search
        // This is the new field for Wave 6
        //[VectorStoreRecordSparseVector)]
        //public SparseVector SparseVector { get; set; }// This is currently not exposed in Semantic Kernel hence the alternative IsFullTextIndexed= ture on text property

        [VectorStoreData]
        public string Parent_Id { get; set; }

        [VectorStoreData]
        public int PageNumber { get; set; } // Crucial metadata for banking compliance

        [VectorStoreData]
        public string ChunkType { get; set; }  //child or parent
    }
}
