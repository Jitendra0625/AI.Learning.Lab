using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    using Microsoft.EntityFrameworkCore;
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;

    public class AuditDbContext : DbContext
    {
        public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

        // Maps to your IngestionAudit database table
        public DbSet<IngestionAuditLog> IngestionAudits { get; set; }

    }

    public class IngestionAuditLog
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public string Id { get; set; }
        public string CorrelationId { get; set; }
        public string BlobName { get; set; }
        public string Status { get; set; }
        public string ErrorDetails { get; set; }
    }
}
