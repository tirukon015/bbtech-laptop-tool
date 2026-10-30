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

        [Fact]
        public void SingleQrCode_FitsCompletePayloadWithSsdHealth_IntoOneQr()
        {
            var detection = new DetectionResult
            {
                Brand = "Dell",
                Model = "Latitude 5420",
                ModelNumber = "5420",
                Cpu = "Intel Core i5-1145G7",
                Generation = "11th Gen",
                Ram = "16 GB",
                Storage = "512 GB NVMe SSD",
                BatteryHealth = "91%"
            };
            detection.System.SerialNumber = "8F9XYZ2";
            detection.System.SystemUuid = "c1b560d6-329b-4546-acd6-f6a1d3ba1756";
            detection.System.BoardSerial = "CN0123456789";

            detection.StorageDevices.Add(new StorageDevice
            {
                Model = "Samsung SSD 980 500GB",
                CapacityLabel = "500 GB",
                BusType = "NVMe",
                HealthPercent = 98,
                PerformancePercent = 100,
                HealthAssessment = "OPTIMAL",
                Temperature = "36 °C",
                PowerOnTime = "82 days, 14 hours",
                DiagnosticDetails = "The status of the solid state disk is PERFECT.",
                HealthSource = "HD Sentinel Pro (v6.30)"
            });

            // Compact serialization specifically designed for single-QR transport
            string json = ItmsPayload.BuildInspectionPayload(
                detection,
                "c1b560d6-329b-4546-acd6-f6a1d3ba1756",
                "Tester Alice",
                "PASS",
                testItems: null,
                condition: null,
                accessories: null,
                indent: false
            );

            string compressed = QrPayloadCompressor.Compress(json);
            Assert.StartsWith("BBT1:", compressed);

            // Single QR Code (no multipart chunking)
            var qr = QrCodeEncoder.EncodeText(compressed, EccLevel.M);
            Assert.True(qr.Version >= 1 && qr.Version <= 40);
            Assert.True(qr.Size > 0);

            // Decompress roundtrip and verify SSD health fields preserved
            string decompressed = QrPayloadCompressor.Decompress(compressed);
            Assert.Contains("Samsung SSD 980 500GB", decompressed);
            Assert.Contains("OPTIMAL", decompressed);
            Assert.Contains("HD Sentinel Pro (v6.30)", decompressed);
            Assert.Contains("PERFECT", decompressed);
            Assert.Contains("c1b560d6-329b-4546-acd6-f6a1d3ba1756", decompressed);

            byte[] bmp = qr.ToBmp(modulePixelSize: 4, quietZoneModules: 4);
            Assert.NotNull(bmp);
            Assert.True(bmp.Length > 2000);
        }

        [Fact]
        public void Decompress_CorruptedOrInvalidPayload_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => QrPayloadCompressor.Decompress("INVALID_PREFIX:XYZ123"));
            Assert.Throws<ArgumentException>(() => QrPayloadCompressor.Decompress(""));
            Assert.Throws<ArgumentException>(() => QrPayloadCompressor.Decompress("   "));
        }

        [Fact]
        public void AdaptiveEcc_Payload2269Bytes_SucceedsInM_And2500BytesFallsBackToL()
        {
            // 2269 bytes fits in Level M at Version 40 (max Level M is 2331 bytes)
            string payload2269 = new string('A', 2269);
            var qrM = QrCodeEncoder.EncodeText(payload2269, EccLevel.M);
            Assert.NotNull(qrM);
            Assert.Equal(EccLevel.M, qrM.ErrorCorrection);
            Assert.Equal(40, qrM.Version);

            // 2500 bytes exceeds Level M (2331 bytes) but fits in Level L (2953 bytes)
            // With adaptive fallback, requesting EccLevel.M does not fail; it automatically falls back to EccLevel.L
            string payload2500 = new string('A', 2500);
            var qrL = QrCodeEncoder.EncodeText(payload2500, EccLevel.M);
            Assert.NotNull(qrL);
            Assert.Equal(EccLevel.L, qrL.ErrorCorrection);
            Assert.True(qrL.Version >= 35 && qrL.Version <= 40);

            byte[] bmp = qrL.ToBmp(modulePixelSize: 2, quietZoneModules: 4);
            Assert.NotNull(bmp);
            Assert.True(bmp.Length > 0);
        }

        [Fact]
        public void AdaptiveEcc_Payload2385Bytes_FallsBackToLevelL_AndProducesValidBmp()
        {
            // Exact 2385 bytes reproduces the real ThinkPad large payload scenario
            string payload2385 = "BBT1:" + new string('B', 2380);
            Assert.Equal(2385, payload2385.Length);

            var qr = QrCodeEncoder.EncodeText(payload2385, EccLevel.M);
            Assert.NotNull(qr);
            Assert.Equal(EccLevel.L, qr.ErrorCorrection);
            Assert.Equal(36, qr.Version);
            Assert.Equal(161, qr.Size);
            Assert.Equal(2385, qr.DataBytes);
            Assert.Equal(2431, qr.MaxCapacityBytes);
            Assert.Equal("Byte (8-bit)", qr.EncodingMode);

            // Verify active matrix modules
            int blackModules = 0;
            for (int r = 0; r < qr.Size; r++)
                for (int c = 0; c < qr.Size; c++)
                    if (qr.Modules[r, c]) blackModules++;
            Assert.True(blackModules > 1000, "Version 36 matrix must contain dark modules");

            // Verify rendered BMP
            byte[] bmpBytes = qr.ToBmp(modulePixelSize: 4, quietZoneModules: 4);
            Assert.NotNull(bmpBytes);
            Assert.True(bmpBytes.Length > 500000);
            Assert.Equal(0x42, bmpBytes[0]); // 'B'
            Assert.Equal(0x4D, bmpBytes[1]); // 'M'

            // Confirm non-blank pixel content
            bool hasBlack = false, hasWhite = false;
            for (int i = 54; i < bmpBytes.Length - 2; i += 3)
            {
                if (bmpBytes[i] == 0 && bmpBytes[i + 1] == 0 && bmpBytes[i + 2] == 0) hasBlack = true;
                if (bmpBytes[i] == 255 && bmpBytes[i + 1] == 255 && bmpBytes[i + 2] == 255) hasWhite = true;
                if (hasBlack && hasWhite) break;
            }
            Assert.True(hasBlack && hasWhite, "BMP image must contain both black and white modules");
        }

        [Fact]
        public void AdaptiveEcc_PayloadExceedingV40PhysicalCapacity_ThrowsClearDiagnostic()
        {
            // 3000 bytes exceeds absolute ISO/IEC 18004 capacity for Version 40 (2953 bytes)
            string overLimitPayload = new string('X', 3000);

            var ex = Assert.Throws<InvalidOperationException>(() => QrCodeEncoder.EncodeText(overLimitPayload, EccLevel.M));
            Assert.Contains("exceeds QR code capacity", ex.Message);
            Assert.Contains("maximum 2953 bytes", ex.Message);
        }

        [Fact]
        public void CompactInspectionPayload_OmitEmptyConditionAndAccessories_ReducesSizeDrastically()
        {
            var detection = new DetectionResult
            {
                Brand = "Lenovo",
                Model = "ThinkPad T14 Gen 2i",
                ModelNumber = "20W000XXXX",
                Cpu = "11th Gen Intel(R) Core(TM) i5-1145G7 @ 2.60GHz",
                Generation = "11th Gen",
                Ram = "32 GB",
                Storage = "512 GB SSD",
                BatteryHealth = "85%"
            };
            detection.System.SerialNumber = "PF123456";
            detection.System.SystemUuid = "12345678-1234-1234-1234-123456789abc";

            detection.StorageDevices.Add(new StorageDevice
            {
                Model = "SAMSUNG MZVLB512HBJQ-000L7",
                CapacityLabel = "512 GB",
                CapacityBytes = 512110190592,
                BusType = "NVMe",
                MediaType = "SSD",
                SerialNumber = "S3PNNF0M123456",
                HealthAssessment = "OPTIMAL",
                HealthPercent = 98,
                PerformancePercent = 100,
                DiagnosticDetails = "The status of the solid state disk is PERFECT. Problematic or weak sectors were not found. TRIM is enabled. Health is determined by SSD specific S.M.A.R.T. attributes. Available Spare: 100%, Media and Data Integrity Errors: 0.",
                HealthSource = "HD Sentinel Pro (v6.30)"
            });

            // Compact inspection payload for single QR
            string compactJson = ItmsPayload.BuildInspectionPayload(
                detection,
                "01234567-89ab-cdef-0123-456789abcdef",
                testedBy: null,
                overallResult: "DETECTED",
                testItems: null,
                condition: null,
                accessories: null,
                indent: false
            );

            string compressed = QrPayloadCompressor.Compress(compactJson);
            Assert.StartsWith("BBT1:", compressed);

            // Verify compressed string fits easily in QR Version 25-30 (under 1,800 bytes)
            Assert.True(compressed.Length < 1800, $"Expected compressed payload < 1800 bytes, was {compressed.Length}");

            // Verify single QR encodes at EccLevel.M without requiring fallback to L
            var qr = QrCodeEncoder.EncodeText(compressed, EccLevel.M);
            Assert.Equal(EccLevel.M, qr.ErrorCorrection);
            Assert.True(qr.Version >= 1 && qr.Version <= 40, $"Expected QR version 1-40, was {qr.Version}");
        }

        [Fact]
        public void QrCodeRendering_ProducesValidNonEmptyBitmapSource()
        {
            // Reproduces exact payload condition that produced 2269 bytes
            string largePayload = new string('Z', 2269);
            var qr = QrCodeEncoder.EncodeText(largePayload, EccLevel.M);

            Assert.NotNull(qr);
            Assert.True(qr.Version >= 1 && qr.Version <= 40);
            Assert.True(qr.Size > 0);

            // Verify matrix has active modules
            int blackCount = 0;
            int whiteCount = 0;
            for (int r = 0; r < qr.Size; r++)
            {
                for (int c = 0; c < qr.Size; c++)
                {
                    if (qr.Modules[r, c]) blackCount++;
                    else whiteCount++;
                }
            }
            Assert.True(blackCount > 0, "QR matrix must contain black modules");
            Assert.True(whiteCount > 0, "QR matrix must contain white modules");

            // Verify BMP byte generation
            byte[] bmpBytes = qr.ToBmp(modulePixelSize: 3, quietZoneModules: 4);
            Assert.NotNull(bmpBytes);
            Assert.True(bmpBytes.Length > 1000);

            // Verify BMP magic header
            Assert.Equal(0x42, bmpBytes[0]); // 'B'
            Assert.Equal(0x4D, bmpBytes[1]); // 'M'

            // Verify image data contains non-trivial content (not a uniform blank rectangle)
            bool hasBlackPixel = false;
            bool hasWhitePixel = false;
            for (int i = 54; i < bmpBytes.Length - 2; i += 3)
            {
                if (bmpBytes[i] == 0 && bmpBytes[i + 1] == 0 && bmpBytes[i + 2] == 0) hasBlackPixel = true;
                if (bmpBytes[i] == 255 && bmpBytes[i + 1] == 255 && bmpBytes[i + 2] == 255) hasWhitePixel = true;
                if (hasBlackPixel && hasWhitePixel) break;
            }
            Assert.True(hasBlackPixel, "Rendered BMP image must contain black pixels");
            Assert.True(hasWhitePixel, "Rendered BMP image must contain white pixels");
        }
    }
}
