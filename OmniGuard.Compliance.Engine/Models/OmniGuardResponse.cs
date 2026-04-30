using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmniGuard.Compliance.Engine.Models
{
    internal class OmniGuardResponse
    {
        public string Answer { get; set; }
        public string AuditorReasoning { get; set; }
        public string Confidence { get; set; } // High, Medium, Low
        public List<int> RetrievedPages { get; set; } = new();
        public string SourceId { get; set; }   // ParentId
    }

}
