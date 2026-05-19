using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    using Microsoft.EntityFrameworkCore;

    public class AuditService
    {
        private readonly AuditDbContext _context;

        public AuditService(AuditDbContext context)
        {
            _context = context;
        }

        public async Task LogStart(IngestionAuditLog logData)
        {
            // Directly references the property values safely inside an interpolated string block
            await _context.Database.ExecuteSqlAsync(
                $"EXECUTE dbo.sp_LogIngestionStart @CorrelationId={logData.CorrelationId}, @BlobName={logData.BlobName}"
            );
        }

        public async Task LogComplete(IngestionAuditLog logData)
        {
            // Directly references the property values safely inside an interpolated string block
            await _context.Database.ExecuteSqlAsync(
                $"EXECUTE dbo.sp_LogIngestionComplete @CorrelationId={logData.CorrelationId}"
            );
        }

        public async Task LogError(IngestionAuditLog logData)
        {
            // Directly references the property values safely inside an interpolated string block
            await _context.Database.ExecuteSqlAsync(
                $"EXECUTE dbo.sp_LogIngestionFinalStatus @CorrelationId={logData.CorrelationId}, @Status={logData.Status}, @Details={logData.ErrorDetails}"
            );
        }
    }
}
