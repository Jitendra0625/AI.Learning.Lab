using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Omniguard.Ingestion
{
    public static class IngestionHashEngine
    {
        /// <summary>
        /// Computes a high-speed, allocation-efficient SHA-256 byte array from raw chunk text.
        /// </summary>
        public static byte[] ComputeSha256(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return new byte[32];
            }

            
            int byteCount = Encoding.UTF8.GetByteCount(content);
            Span<byte> sourceBytes = byteCount <= 4096
                ? stackalloc byte[byteCount]
                : new byte[byteCount];

            Encoding.UTF8.GetBytes(content, sourceBytes);

            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(sourceBytes, hashBytes);

            return hashBytes.ToArray();
        }
    }
}
