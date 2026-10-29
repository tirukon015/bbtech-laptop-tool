using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Settings;
using Xunit;

namespace BBTech.Core.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage LastRequest { get; private set; }
        public string LastRequestBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; } = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"success\":true,\"status\":\"queued\",\"request_no\":1002,\"item_id\":\"item-uuid-123\"}")
        };
        public Exception ExceptionToThrow { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync();
            }

            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return ResponseToReturn;
        }
    }

    public class ItmsIntegrationTests
    {
        [Fact]
        public void IsConfigured_ReturnsTrueOnlyWhenUrlAndKeyProvided()
        {
            var c1 = new HttpItmsClient(() => "", () => "");
            Assert.False(c1.IsConfigured);

            var c2 = new HttpItmsClient(() => "http://localhost:8000", () => "");
            Assert.False(c2.IsConfigured);

            var c3 = new HttpItmsClient(() => "http://localhost:8000", () => "my-key");
            Assert.True(c3.IsConfigured);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_SendsAgreedHeadersAndPayload()
        {
            var handler = new MockHttpMessageHandler();
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "secret-key-xyz", handler);

            var detection = new DetectionResult
            {
                Brand = "Lenovo",
                Model = "ThinkPad T480",
                ModelNumber = "20L5",
                Cpu = "Intel Core i5-8350U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "256 GB SSD",
                BatteryHealth = "85%",
                ToolVersion = "1.0.0",
            };
            detection.System.SerialNumber = "PF123456";
            detection.System.SystemUuid = "01234567-89AB-CDEF-0123-456789ABCDEF";

            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.True(result.Success);
            Assert.Equal(1002, result.RequestNo);
            Assert.Equal("item-uuid-123", result.DeviceId);
            Assert.Equal(detection.SubmissionId, result.RequestId);

            // Verify HTTP Request
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
            Assert.Equal("https://itms.local.dev/api/devices/requests", handler.LastRequest.RequestUri.ToString());

            // Verify Auth Header
            Assert.True(handler.LastRequest.Headers.Contains("x-itms-api-key"));
            Assert.Contains("secret-key-xyz", handler.LastRequest.Headers.GetValues("x-itms-api-key"));

            // Verify User-Agent
            Assert.True(handler.LastRequest.Headers.Contains("User-Agent"));

            // Verify Payload contains stable SubmissionId
            Assert.Contains(detection.SubmissionId, handler.LastRequestBody);
            Assert.Contains("\"schema_version\": \"1.0\"", handler.LastRequestBody);
            Assert.Contains("PF123456", handler.LastRequestBody);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles401UnauthorizedWithoutExposingKey()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Unauthorized\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "wrong-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("authentication failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wrong-key", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles422ValidationError()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage((HttpStatusCode)422)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"No valid hardware identity found\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("validation failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No valid hardware identity found", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles400InvalidJsonError()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Invalid JSON body\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("ITMS validation failed: Invalid JSON body", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_Handles413PayloadTooLargeError()
        {
            var handler = new MockHttpMessageHandler
            {
                ResponseToReturn = new HttpResponseMessage((HttpStatusCode)413)
                {
                    Content = new StringContent("{\"success\":false,\"error\":\"Payload too large. Maximum allowed size is 1MB.\"}")
                }
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("Payload too large", result.Message);
        }

        [Fact]
        public async Task SubmitNewDeviceAsync_HandlesTimeoutGracefully()
        {
            var handler = new MockHttpMessageHandler
            {
                ExceptionToThrow = new TaskCanceledException("Timed out")
            };
            var client = new HttpItmsClient(() => "https://itms.local.dev", () => "valid-key", handler);

            var detection = new DetectionResult { Brand = "Lenovo" };
            var result = await client.SubmitNewDeviceAsync(detection);

            Assert.False(result.Success);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ItmsPayload_BuildInspectionPayload_GeneratesValidJsonStructure()
        {
            var detection = new DetectionResult
            {
                Brand = "Dell",
                Model = "Latitude 7490",
                ModelNumber = "7490",
                Cpu = "i7-8650U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "512 GB SSD",
                BatteryHealth = "90%",
                ToolVersion = "1.0.0"
            };
            detection.System.SerialNumber = "ABC1234";

            string json = ItmsPayload.BuildInspectionPayload(detection, "req-uuid-999", "Tester Bob", "PASS");

            Assert.Contains("\"schema_version\": \"1.0\"", json);
            Assert.Contains("\"request_id\": \"req-uuid-999\"", json);
            Assert.Contains("\"overall_result\": \"PASS\"", json);
            Assert.Contains("\"tested_by\": \"Tester Bob\"", json);
            Assert.Contains("\"serial_number\": \"ABC1234\"", json);
            Assert.Contains("\"brand\": \"Dell\"", json);
        }

        [Fact]
        public void ItmsPayload_BuildInspectionPayload_SerializesAllComprehensiveHardwareAndQCSections()
        {
            var detection = new DetectionResult
            {
                Brand = "Lenovo",
                Model = "ThinkPad T480",
                ModelNumber = "20L5",
                Cpu = "Intel Core i5-8350U",
                Generation = "8th Gen",
                Ram = "16 GB",
                Storage = "512 GB SSD",
                BatteryHealth = "88%",
                ToolVersion = "1.0.0"
            };
            detection.System.SerialNumber = "PF123456";
            detection.System.SystemUuid = "01234567-89AB-CDEF-0123-456789ABCDEF";
            detection.System.BoardSerial = "L1HF123456";
            detection.System.BiosVersion = "N24ET61W (1.36 )";
            detection.System.BiosReleaseDate = "2021-08-10";
            detection.System.ComputerName = "QC-BENCH-01";
            detection.System.ChassisType = "Notebook";
            detection.System.IsPortable = true;
            detection.System.OperatingSystem = "Windows 11 Pro 23H2";

            detection.CpuDetails.FullName = "Intel Core i5-8350U";
            detection.CpuDetails.Cores = 4;
            detection.CpuDetails.LogicalProcessors = 8;
            detection.CpuDetails.MaxClockMhz = 1900;

            detection.Memory.TotalBytes = 17179869184;
            detection.Memory.FromModules = true;
            detection.Memory.Modules.Add(new MemoryModule
            {
                CapacityBytes = 17179869184,
                SpeedMhz = 2400,
                Type = "DDR4",
                Slot = "ChannelA-DIMM0",
                Manufacturer = "Samsung",
                PartNumber = "M471A2K43CB1-CRC"
            });

            detection.StorageDevices.Add(new StorageDevice
            {
                Model = "SAMSUNG MZVLB512HAJQ-000L7",
                CapacityBytes = 512110190592,
                CapacityLabel = "512 GB",
                MediaType = "SSD",
                BusType = "NVMe",
                SerialNumber = "S3W8NX0K123456",
                WindowsHealth = "Healthy",
                WearPercentUsed = 5,
                PowerOnHours = 1250,
                TemperatureC = 34
            });

            detection.Battery.Present = true;
            detection.Battery.BatteryCount = 1;
            detection.Battery.Name = "01AV421";
            detection.Battery.Manufacturer = "LGC";
            detection.Battery.DesignCapacityMWh = 24000;
            detection.Battery.FullChargeCapacityMWh = 21120;
            detection.Battery.CurrentCapacityMWh = 20000;
            detection.Battery.ChargePercent = 95.0;
            detection.Battery.CycleCount = 120;
            detection.Battery.HealthPercent = 88.0;
            detection.Battery.AcAdapterConnected = true;
            detection.Battery.ChargingStatus = "AC Connected";
            detection.Battery.Source = "BatteryInfoView";

            detection.Gpus.Add(new GpuDetails
            {
                Name = "Intel UHD Graphics 620",
                Manufacturer = "Intel Corporation",
                DedicatedVramBytes = 134217728,
                DedicatedVramLabel = "128 MB",
                DriverVersion = "31.0.101.2115"
            });

            detection.Display.DisplayCount = 1;
            detection.Display.Resolution = "1920 x 1080";
            detection.Display.RefreshRateHz = 60;
            detection.Display.PhysicalSizeInches = 14.0;
            detection.Display.TouchscreenSupported = false;

            detection.Peripherals.SpeakerName = "Realtek High Definition Audio";
            detection.Peripherals.WifiAdapter = "Intel(R) Dual Band Wireless-AC 8265";
            detection.Peripherals.BluetoothAdapter = "Intel(R) Wireless Bluetooth(R)";
            detection.Peripherals.FingerprintReader = "Synaptics WBDI - SGX";
            detection.Peripherals.TpmPresent = true;
            detection.Peripherals.TpmVersion = "2.0";
            detection.Peripherals.SecureBootEnabled = true;

            var condition = new PhysicalConditionDetails
            {
                Grade = "Grade A",
                ScreenCondition = "Clean, no scratches",
                LidCover = "Minor scuff on corner",
                PalmRest = "Clean",
                KeyboardCondition = "All keys intact",
                Hinges = "Firm",
                MissingScrews = "None",
                Notes = "Excellent condition overall"
            };

            var accessories = new AccessoryDetails
            {
                ChargerIncluded = true,
                ChargerManufacturer = "Lenovo",
                ChargerWattage = 65,
                ConnectorType = "USB-C",
                Notes = "Original 65W charger included"
            };

            var testItems = new List<TestItem>
            {
                new TestItem(HardwareTest.Display, "Display & Dead Pixel Check") { State = TestState.Pass, Notes = "No dead pixels" },
                new TestItem(HardwareTest.Keyboard, "Keyboard Test") { State = TestState.Pass, Notes = "All keys registered" },
                new TestItem(HardwareTest.Touchpad, "Touchpad Test") { State = TestState.Pass, Notes = "Gestures working" },
                new TestItem(HardwareTest.Battery, "Battery Health Check") { State = TestState.Pass, Notes = "88% health" }
            };

            string json = ItmsPayload.BuildInspectionPayload(
                detection,
                "01234567-89ab-cdef-0123-456789abcdef",
                "QC Tech Alex",
                "PASS",
                testItems,
                condition,
                accessories);

            // Verify top-level fields
            Assert.Contains("\"schema_version\": \"1.0\"", json);
            Assert.Contains("\"request_id\": \"01234567-89ab-cdef-0123-456789abcdef\"", json);
            Assert.Contains("\"overall_result\": \"PASS\"", json);
            Assert.Contains("\"tested_by\": \"QC Tech Alex\"", json);

            // Verify System Details
            Assert.Contains("\"system_details\"", json);
            Assert.Contains("\"serial_number\": \"PF123456\"", json);
            Assert.Contains("\"system_uuid\": \"01234567-89AB-CDEF-0123-456789ABCDEF\"", json);
            Assert.Contains("\"board_serial\": \"L1HF123456\"", json);
            Assert.Contains("\"computer_name\": \"QC-BENCH-01\"", json);
            Assert.Contains("\"operating_system\": \"Windows 11 Pro 23H2\"", json);

            // Verify CPU Details
            Assert.Contains("\"cpu_details\"", json);
            Assert.Contains("\"cores\": 4", json);
            Assert.Contains("\"threads\": 8", json);

            // Verify Memory Modules
            Assert.Contains("\"memory_details\"", json);
            Assert.Contains("\"part_number\": \"M471A2K43CB1-CRC\"", json);

            // Verify Storage Devices
            Assert.Contains("\"storage_devices\"", json);
            Assert.Contains("\"wear_percent\": 5", json);
            Assert.Contains("\"serial_number\": \"S3W8NX0K123456\"", json);

            // Verify Battery Details
            Assert.Contains("\"battery_details\"", json);
            Assert.Contains("\"cycle_count\": 120", json);
            Assert.Contains("\"charging_status\": \"AC Connected\"", json);

            // Verify GPU & Display & Peripherals
            Assert.Contains("\"gpus\"", json);
            Assert.Contains("\"Intel UHD Graphics 620\"", json);
            Assert.Contains("\"display\"", json);
            Assert.Contains("\"1920 x 1080\"", json);
            Assert.Contains("\"peripherals\"", json);
            Assert.Contains("\"tpm_present\": true", json);

            // Verify Condition & Accessories
            Assert.Contains("\"condition\"", json);
            Assert.Contains("\"grade\": \"Grade A\"", json);
            Assert.Contains("\"accessories\"", json);
            Assert.Contains("\"charger_wattage\": 65", json);

            // Verify QC Tests
            Assert.Contains("\"tests\"", json);
            Assert.Contains("\"test_id\": \"Keyboard\"", json);
            Assert.Contains("\"status\": \"PASS\"", json);
        }
    }
}
