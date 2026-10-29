using System;
using System.Collections.Generic;
using System.Text;
using BBTech.Core.Models;
using BBTech.Core.Qr;
using BBTech.Core.Integration;
using Xunit;

namespace BBTech.Core.Tests
{
    public class QrCodeTests
    {
        [Fact]
        public void QrCodeEncoder_EncodeText_ProducesValidMatrixDimensions()
        {
            // Short text fits in Version 1 (21x21 modules)
            var qr1 = QrCodeEncoder.EncodeText("HELLO", EccLevel.M);
            Assert.Equal(1, qr1.Version);
            Assert.Equal(21, qr1.Size);
            Assert.NotNull(qr1.Modules);
            Assert.Equal(21, qr1.Modules.GetLength(0));
            Assert.Equal(21, qr1.Modules.GetLength(1));

            // Medium text fits in Version 2 (25x25 modules)
            var qr2 = QrCodeEncoder.EncodeText("This is a somewhat longer text that requires Version 2 QR matrix", EccLevel.M);
            Assert.True(qr2.Version >= 2);
            Assert.Equal(17 + 4 * qr2.Version, qr2.Size);
        }

        [Fact]
        public void QrCodeEncoder_ToBmp_GeneratesValidBmpHeader()
        {
            var qr = QrCodeEncoder.EncodeText("BBTech Test 12345", EccLevel.M);
            byte[] bmp = qr.ToBmp(modulePixelSize: 4, quietZoneModules: 4);

            Assert.NotNull(bmp);
            Assert.True(bmp.Length > 54);

            // BMP Magic Number 'BM'
            Assert.Equal(0x42, bmp[0]);
            Assert.Equal(0x4D, bmp[1]);

            // File Size
            int fileSize = bmp[2] | (bmp[3] << 8) | (bmp[4] << 16) | (bmp[5] << 24);
            Assert.Equal(bmp.Length, fileSize);

            // Pixel Data Offset
            int offset = bmp[10] | (bmp[11] << 8) | (bmp[12] << 16) | (bmp[13] << 24);
            Assert.Equal(54, offset);

            // DIB Header Size
            int dibSize = bmp[14] | (bmp[15] << 8) | (bmp[16] << 16) | (bmp[17] << 24);
            Assert.Equal(40, dibSize);

            // Bits per pixel = 24
            int bpp = bmp[28] | (bmp[29] << 8);
            Assert.Equal(24, bpp);
        }

        [Fact]
        public void QrPayloadCompressor_RoundTrip_PreservesExactJson()
        {
            string sampleJson = "{\"schema_version\":\"1.0\",\"request_id\":\"c1b560d6-329b-4546-acd6-f6a1d3ba1756\",\"device\":{\"brand\":\"Lenovo\",\"model\":\"ThinkPad T480\",\"serial_number\":\"PF123456\"}}";

            string compressed = QrPayloadCompressor.Compress(sampleJson);
            Assert.StartsWith("BBT1:", compressed);

            string decompressed = QrPayloadCompressor.Decompress(compressed);
            Assert.Equal(sampleJson, decompressed);
        }

        [Fact]
        public void QrPayloadCompressor_Decompress_HandlesRawJson()
        {
            string raw = "{\"schema_version\":\"1.0\",\"serial\":\"PF123456\"}";
            string result = QrPayloadCompressor.Decompress(raw);
            Assert.Equal(raw, result);
        }

        [Fact]
        public void QrPayloadCompressor_Chunk_SplitsOversizedPayload()
        {
            string payload = new string('A', 2500);
            string reqId = "test-req-id-12345";
            var chunks = QrPayloadCompressor.Chunk(payload, maxChunkSize: 1000, requestId: reqId);

            Assert.Equal(3, chunks.Count);
            Assert.StartsWith("BBT1:PART:1:3:test-req-id-12345:", chunks[0]);
            Assert.StartsWith("BBT1:PART:2:3:test-req-id-12345:", chunks[1]);
            Assert.StartsWith("BBT1:PART:3:3:test-req-id-12345:", chunks[2]);
        }

        [Fact]
        public void QrPayloadCompressor_Measure_ReportsCorrectMetrics()
        {
            string json = "{\"brand\":\"Lenovo\",\"model\":\"ThinkPad T480\",\"cpu\":\"Intel Core i5-8350U\",\"ram\":\"16 GB\",\"storage\":\"256 GB SSD\",\"battery\":\"85%\"}";
            var (rawBytes, compBytes, ratio) = QrPayloadCompressor.Measure(json);

            Assert.True(rawBytes > 0);
            Assert.True(compBytes > 0);
            Assert.True(ratio > 0);
        }

        [Fact]
        public void FullInspectionPayload_GeneratesValidQrCodeAndBmp()
        {
            var detection = new DetectionResult
            {
                Brand = "Lenovo",
                Model = "ThinkPad T480",
                ModelNumber = "20L5-S02C00",
                Cpu = "Intel Core i5-8350U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "256 GB SSD",
                BatteryHealth = "85%"
            };
            detection.System.SerialNumber = "PF123456";
            detection.System.SystemUuid = "01234567-89AB-CDEF-0123-456789ABCDEF";
            detection.System.BoardSerial = "L1HF88800AB";

            string json = ItmsPayload.BuildInspectionPayload(detection, "c1b560d6-329b-4546-acd6-f6a1d3ba1756", "Operator", "PASS");
            string compressed = QrPayloadCompressor.Compress(json);

            var qr = QrCodeEncoder.EncodeText(compressed, EccLevel.L);
            Assert.True(qr.Version >= 10);
            Assert.True(qr.Size >= 57);

            byte[] bmp = qr.ToBmp(modulePixelSize: 4);
            Assert.True(bmp.Length > 1000);
            Assert.Equal(0x42, bmp[0]);
            Assert.Equal(0x4D, bmp[1]);
        }
    }
}
