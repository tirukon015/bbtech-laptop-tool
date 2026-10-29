using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BBTech.Core.Qr
{
    /// <summary>
    /// Handles Gzip compression and Base64 framing for the BBTech optical protocol (BBT1).
    /// Drastically reduces QR payload footprint while preserving 100% of the JSON data.
    /// Provides transparent fallback to uncompressed JSON and multi-part chunking.
    /// </summary>
    public static class QrPayloadCompressor
    {
        public const string Prefix = "BBT1:";
        public const string MultiPartPrefix = "BBT1:PART:";

        /// <summary>
        /// Compresses a JSON string using Gzip and returns a prefixed Base64 string ("BBT1:...").
        /// </summary>
        public static string Compress(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("JSON payload cannot be empty", nameof(json));

            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            using (var memoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal, true))
                {
                    gzipStream.Write(utf8, 0, utf8.Length);
                }
                byte[] compressed = memoryStream.ToArray();
                return Prefix + Convert.ToBase64String(compressed);
            }
        }

        /// <summary>
        /// Decompresses a BBT1 prefixed string or returns the raw JSON if uncompressed.
        /// </summary>
        public static string Decompress(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Payload cannot be empty", nameof(payload));

            string trimmed = payload.Trim();

            // Direct uncompressed JSON fallback
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                return trimmed;
            }

            if (trimmed.StartsWith(Prefix))
            {
                string base64 = trimmed.Substring(Prefix.Length);
                byte[] compressed = Convert.FromBase64String(base64);

                using (var compressedStream = new MemoryStream(compressed))
                using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }

            throw new FormatException("Unrecognized QR payload format. Expected 'BBT1:' prefix or raw JSON.");
        }

        /// <summary>
        /// Splits a payload into multi-part frames if it exceeds maxChunkSize.
        /// Format: BBT1:PART:{index}:{total}:{requestId}:{chunk}
        /// </summary>
        public static List<string> Chunk(string payload, int maxChunkSize = 900, string requestId = null)
        {
            if (string.IsNullOrEmpty(payload)) throw new ArgumentNullException(nameof(payload));
            if (maxChunkSize < 200) maxChunkSize = 200;

            var chunks = new List<string>();

            if (payload.Length <= maxChunkSize)
            {
                chunks.Add(payload);
                return chunks;
            }

            string id = !string.IsNullOrEmpty(requestId) ? requestId : Guid.NewGuid().ToString("D");
            int totalParts = (int)Math.Ceiling((double)payload.Length / maxChunkSize);

            for (int i = 0; i < totalParts; i++)
            {
                int start = i * maxChunkSize;
                int length = Math.Min(maxChunkSize, payload.Length - start);
                string slice = payload.Substring(start, length);
                chunks.Add($"{MultiPartPrefix}{i + 1}:{totalParts}:{id}:{slice}");
            }

            return chunks;
        }

        /// <summary>
        /// Measures uncompressed vs compressed sizes for logging and UI telemetry.
        /// </summary>
        public static (int RawBytes, int CompressedBytes, double Ratio) Measure(string json)
        {
            if (string.IsNullOrEmpty(json)) return (0, 0, 0);

            byte[] raw = Encoding.UTF8.GetBytes(json);
            string compressed = Compress(json);
            byte[] comp = Encoding.UTF8.GetBytes(compressed);

            double ratio = raw.Length > 0 ? (double)comp.Length / raw.Length : 0;
            return (raw.Length, comp.Length, ratio);
        }
    }
}
