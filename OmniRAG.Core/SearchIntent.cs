using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmniRAG.Core
{
    internal class SearchIntent
    {
        public string Category { get; set; } = "General";
        public int Year { get; set; } = 2024;
        public string RefinedQuery { get; set; }
    }
}
