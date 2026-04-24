using Microsoft.Extensions.VectorData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmniGuard.Compliance.Engine.Models
{
    internal class ComplianceRecord
    {
        [VectorStoreKey]
        public string Id { get; set; }

        [VectorStoreData]
        public string Text {  get; set; }

        [VectorStoreVector(384)]
        public ReadOnlyMemory<float> Vector { get; set; }
        
        [VectorStoreData]
        public string Parent_Id { get; set; }

        [VectorStoreData]
        public int PageNumber {  get; set; } // Crucial metadata for banking compliance

        [VectorStoreData]
        public string ChunkType { get; set; }  //child or parent

    }
}
