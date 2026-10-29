using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BBTech.Core.Export;
using BBTech.Core.Models;
using BBTech.Core.Testing;

namespace BBTech.Core.Integration
{
    /// <summary>Result of sending a device to ITMS.</summary>
    public sealed class ItmsSubmitResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        /// <summary>ITMS identifier of the created device, when the API returns one.</summary>
        public string DeviceId { get; set; } = "";
        /// <summary>Human-readable request number assigned by ITMS (e.g. 1002).</summary>
        public long RequestNo { get; set; }
        /// <summary>Idempotency UUID sent to ITMS.</summary>
        public string RequestId { get; set; } = "";
        /// <summary>Submission status returned by ITMS (e.g. "queued" or "already_received").</summary>
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// Service contract for submitting detected laptops to the ITMS review queue.
    /// </summary>
    public interface IItmsClient
    {
        bool IsConfigured { get; }
        Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection);
    }

    public sealed class NotConfiguredItmsClient : IItmsClient
    {
        public bool IsConfigured => false;

        public Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection) =>
            Task.FromResult(new ItmsSubmitResult
            {
                Success = false,
                Message = "ITMS integration is not configured yet."
            });
    }

    /// <summary>
    /// Real HTTP client that submits detection records to the ITMS review queue API.
    /// Fully idempotent using detection.SubmissionId.
    /// </summary>
    public sealed class HttpItmsClient : IItmsClient
    {
        private readonly Func<string> _baseUrlProvider;
        private readonly Func<string> _apiKeyProvider;
        private readonly HttpMessageHandler _customHandler;

        public HttpItmsClient(Func<string> baseUrlProvider, Func<string> apiKeyProvider, HttpMessageHandler customHandler = null)
        {
            _baseUrlProvider = baseUrlProvider ?? (() => "");
            _apiKeyProvider = apiKeyProvider ?? (() => "");
            _customHandler = customHandler;
        }

        public bool IsConfigured
        {
            get
            {
                var url = _baseUrlProvider();
                var key = _apiKeyProvider();
                return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
            }
        }

        public async Task<ItmsSubmitResult> SubmitNewDeviceAsync(DetectionResult detection)
        {
            if (detection == null)
            {
                return new ItmsSubmitResult { Success = false, Message = "No detection result available to submit." };
            }

            var baseUrl = (_baseUrlProvider() ?? "").Trim().TrimEnd('/');
            var apiKey = (_apiKeyProvider() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    Message = "ITMS is not configured. Set the ITMS Server URL and API key in Settings or environment variables (ITMS_URL, ITMS_API_KEY)."
                };
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri))
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    Message = "Invalid ITMS Server URL: \"" + baseUrl + "\". Please enter a valid HTTP or HTTPS address."
                };
            }

            var endpoint = baseUrl + "/api/devices/requests";
            var requestId = detection.SubmissionId;
            var jsonPayload = ItmsPayload.BuildInspectionPayload(detection, requestId);

            try
            {
                // Force TLS 1.2 compatibility on .NET 4.7.2
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                using (var client = _customHandler != null ? new HttpClient(_customHandler, false) : new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BBTech-LaptopTool/" + (string.IsNullOrEmpty(detection.ToolVersion) ? "1.0.0" : detection.ToolVersion));
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-itms-api-key", apiKey);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await client.PostAsync(endpoint, content).ConfigureAwait(false))
                    {
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            long reqNo = 0;
                            string devId = "";
                            string status = "";

                            var reqNoMatch = Regex.Match(responseBody, @"""request_no""\s*:\s*(\d+)");
                            if (reqNoMatch.Success) long.TryParse(reqNoMatch.Groups[1].Value, out reqNo);

                            var itemIdMatch = Regex.Match(responseBody, @"""item_id""\s*:\s*""([^""]+)""");
                            if (itemIdMatch.Success) devId = itemIdMatch.Groups[1].Value;

                            var statusMatch = Regex.Match(responseBody, @"""status""\s*:\s*""([^""]+)""");
                            if (statusMatch.Success) status = statusMatch.Groups[1].Value;

                            string msg = reqNo > 0
                                ? (status == "already_received" ? "Already received by ITMS as Request #" + reqNo : "Queued in ITMS as Request #" + reqNo)
                                : "Successfully received by ITMS.";

                            return new ItmsSubmitResult
                            {
                                Success = true,
                                RequestNo = reqNo,
                                RequestId = requestId,
                                DeviceId = devId,
                                Status = status,
                                Message = msg
                            };
                        }

                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return new ItmsSubmitResult
                            {
                                Success = false,
                                RequestId = requestId,
                                Message = "ITMS authentication failed. Verify the API key in Settings."
                            };
                        }

                        if ((int)response.StatusCode == 400 || (int)response.StatusCode == 413 || (int)response.StatusCode == 422)
                        {
                            var errMsgMatch = Regex.Match(responseBody, @"""error""\s*:\s*""([^""]+)""");
                            string errDetail = errMsgMatch.Success ? errMsgMatch.Groups[1].Value : "Validation rejected by ITMS.";
                            return new ItmsSubmitResult
                            {
                                Success = false,
                                RequestId = requestId,
                                Message = "ITMS validation failed: " + errDetail
                            };
                        }

                        return new ItmsSubmitResult
                        {
                            Success = false,
                            RequestId = requestId,
                            Message = "ITMS responded with HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ")."
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Connection timed out connecting to ITMS (" + baseUrl + "). Check your network and try again."
                };
            }
            catch (HttpRequestException ex)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Network error connecting to ITMS: " + ex.Message
                };
            }
            catch (Exception ex)
            {
                return new ItmsSubmitResult
                {
                    Success = false,
                    RequestId = requestId,
                    Message = "Unexpected submission error: " + ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Future synchronisation of the laptop's local QC history to ITMS.
    /// </summary>
    public interface ISyncService
    {
        bool IsEnabled { get; }
        Task<Storage.SyncRecord> SyncInspectionAsync(Storage.DeviceRecord device, Storage.InspectionView inspection, Storage.SyncRecord current);
    }

    public sealed class LocalOnlySyncService : ISyncService
    {
        public bool IsEnabled => false;

        public Task<Storage.SyncRecord> SyncInspectionAsync(Storage.DeviceRecord device, Storage.InspectionView inspection, Storage.SyncRecord current) =>
            Task.FromResult(current);
    }

    public static class ItmsPayload
    {
        /// <summary>The JSON body agreed for ITMS (the eight primary fields).</summary>
        public static string Build(DetectionResult detection) => JsonWriter.Serialize(DetectionExporter.ToItmsObject(detection));

        /// <summary>Builds the full inspection payload complying with ITMS contract version 1.0.</summary>
        public static string BuildInspectionPayload(
            DetectionResult detection,
            string requestId,
            string testedBy = null,
            string overallResult = "DETECTED",
            IEnumerable<TestItem> testItems = null,
            PhysicalConditionDetails condition = null,
            AccessoryDetails accessories = null)
        {
            var d = detection ?? new DetectionResult();
            var deviceObj = DetectionExporter.ToItmsObject(d);
            deviceObj.Add("serial_number", string.IsNullOrWhiteSpace(d.System?.SerialNumber) ? DetectionResult.Unknown : d.System.SerialNumber);
            deviceObj.Add("system_uuid", d.System?.SystemUuid ?? "");
            deviceObj.Add("board_serial", d.System?.BoardSerial ?? "");

            var root = new JsonObject
            {
                { "schema_version", "1.0" },
                { "request_id", !string.IsNullOrEmpty(requestId) ? requestId : d.SubmissionId },
                { "source", "bbtech-laptop-tool" },
                { "tool_version", d.ToolVersion ?? "1.0.0" },
                { "inspection_date", d.DetectedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "inspection_time", d.DetectedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) },
                { "overall_result", overallResult ?? "DETECTED" },
            };

            if (!string.IsNullOrEmpty(testedBy)) root.Add("tested_by", testedBy);

            root.Add("device", deviceObj);

            // 1. System Identity
            root.Add("system_details", new JsonObject
            {
                { "manufacturer", d.System?.RawManufacturer ?? "" },
                { "model", d.System?.RawModel ?? "" },
                { "system_family", d.System?.SystemFamily ?? "" },
                { "product_version", d.System?.ProductVersion ?? "" },
                { "product_code", d.System?.ProductCode ?? "" },
                { "system_sku", d.System?.SystemSku ?? "" },
                { "serial_number", d.System?.SerialNumber ?? "" },
                { "system_uuid", d.System?.SystemUuid ?? "" },
                { "board_serial", d.System?.BoardSerial ?? "" },
                { "bios_version", d.System?.BiosVersion ?? "" },
                { "bios_release_date", d.System?.BiosReleaseDate ?? "" },
                { "firmware_asset_tag", d.System?.FirmwareAssetTag ?? "" },
                { "computer_name", d.System?.ComputerName ?? "" },
                { "chassis_type", d.System?.ChassisType ?? "" },
                { "is_portable", (object)d.System?.IsPortable },
                { "os", d.System?.OperatingSystem ?? "" },
            });

            // 2. CPU Details
            root.Add("cpu_details", new JsonObject
            {
                { "full_name", d.CpuDetails?.FullName ?? "" },
                { "vendor", d.CpuDetails?.Vendor ?? "" },
                { "model_code", d.CpuDetails?.ModelCode ?? "" },
                { "cores", d.CpuDetails?.Cores ?? 0 },
                { "threads", d.CpuDetails?.LogicalProcessors ?? 0 },
                { "max_clock_mhz", d.CpuDetails?.MaxClockMhz ?? 0 },
            });

            // 3. Memory Details
            var memObj = new JsonObject
            {
                { "total_bytes", d.Memory?.TotalBytes ?? 0 },
                { "from_modules", d.Memory?.FromModules ?? false },
            };
            if (d.Memory?.Modules != null)
            {
                memObj.Add("modules", d.Memory.Modules.Select(m => new JsonObject
                {
                    { "capacity_bytes", m.CapacityBytes },
                    { "speed_mhz", m.SpeedMhz },
                    { "type", m.Type },
                    { "slot", m.Slot },
                    { "manufacturer", m.Manufacturer },
                    { "part_number", m.PartNumber },
                }).ToList());
            }
            root.Add("memory_details", memObj);

            // 4. Storage Devices
            if (d.StorageDevices != null)
            {
                root.Add("storage_devices", d.StorageDevices.Select(s => new JsonObject
                {
                    { "model", s.Model },
                    { "capacity_bytes", s.CapacityBytes },
                    { "capacity_label", s.CapacityLabel },
                    { "media_type", s.MediaType },
                    { "bus_type", s.BusType },
                    { "serial_number", s.SerialNumber },
                    { "partitions_summary", s.PartitionsSummary },
                    { "windows_health", s.WindowsHealth },
                    { "is_external", s.IsExternal },
                    { "device_id", s.DeviceId },
                    { "wear_percent", (object)s.WearPercentUsed },
                    { "power_on_hours", (object)s.PowerOnHours },
                    { "temperature_c", (object)s.TemperatureC },
                    { "health_percent", (object)s.HealthPercent },
                    { "performance_percent", (object)s.PerformancePercent },
                    { "power_on_time", s.PowerOnTime },
                }).ToList());
            }

            // 5. Battery Details
            root.Add("battery_details", new JsonObject
            {
                { "present", d.Battery?.Present ?? false },
                { "battery_count", d.Battery?.BatteryCount ?? 0 },
                { "name", d.Battery?.Name ?? "" },
                { "manufacturer", d.Battery?.Manufacturer ?? "" },
                { "chemistry", d.Battery?.Chemistry ?? "" },
                { "power_state", d.Battery?.PowerState ?? "" },
                { "design_capacity_mwh", (object)d.Battery?.DesignCapacityMWh },
                { "full_charge_capacity_mwh", (object)d.Battery?.FullChargeCapacityMWh },
                { "current_capacity_mwh", (object)d.Battery?.CurrentCapacityMWh },
                { "charge_percent", (object)d.Battery?.ChargePercent },
                { "cycle_count", (object)d.Battery?.CycleCount },
                { "health_percent", (object)d.Battery?.HealthPercent },
                { "ac_adapter_connected", (object)d.Battery?.AcAdapterConnected },
                { "charging_status", d.Battery?.ChargingStatus ?? "" },
                { "source", d.Battery?.Source ?? "" },
            });

            // 6. Graphics
            if (d.Gpus != null)
            {
                root.Add("gpus", d.Gpus.Select(g => new JsonObject
                {
                    { "name", g.Name },
                    { "manufacturer", g.Manufacturer },
                    { "dedicated_vram_bytes", (object)g.DedicatedVramBytes },
                    { "dedicated_vram_label", g.DedicatedVramLabel },
                    { "driver_version", g.DriverVersion },
                }).ToList());
            }

            // 7. Display
            root.Add("display", new JsonObject
            {
                { "display_count", d.Display?.DisplayCount ?? 1 },
                { "resolution", d.Display?.Resolution ?? "" },
                { "refresh_rate_hz", (object)d.Display?.RefreshRateHz },
                { "physical_size_inches", (object)d.Display?.PhysicalSizeInches },
                { "panel_model", d.Display?.PanelModel ?? "" },
                { "touchscreen", (object)d.Display?.TouchscreenSupported },
                { "pen", (object)d.Display?.PenSupported },
            });

            // 8. Peripherals & Firmware Security
            root.Add("peripherals", new JsonObject
            {
                { "webcam", d.Peripherals?.WebcamName ?? "" },
                { "microphone", d.Peripherals?.MicrophoneName ?? "" },
                { "speaker", d.Peripherals?.SpeakerName ?? "" },
                { "wifi_adapter", d.Peripherals?.WifiAdapter ?? "" },
                { "bluetooth_adapter", d.Peripherals?.BluetoothAdapter ?? "" },
                { "ethernet_adapter", d.Peripherals?.EthernetAdapter ?? "" },
                { "fingerprint_reader", d.Peripherals?.FingerprintReader ?? "" },
                { "tpm_present", (object)d.Peripherals?.TpmPresent },
                { "tpm_version", d.Peripherals?.TpmVersion ?? "" },
                { "secure_boot_enabled", (object)d.Peripherals?.SecureBootEnabled },
            });

            // 9. Physical Condition
            var cond = condition ?? d.Condition ?? new PhysicalConditionDetails();
            root.Add("condition", new JsonObject
            {
                { "grade", cond.Grade ?? "" },
                { "screen", cond.ScreenCondition ?? "" },
                { "lid_cover", cond.LidCover ?? "" },
                { "palm_rest", cond.PalmRest ?? "" },
                { "keyboard", cond.KeyboardCondition ?? "" },
                { "bottom_cover", cond.BottomCover ?? "" },
                { "hinges", cond.Hinges ?? "" },
                { "missing_screws", cond.MissingScrews ?? "" },
                { "rubber_feet", cond.RubberFeet ?? "" },
                { "port_damage", cond.PortDamage ?? "" },
                { "missing_keys", cond.MissingKeys ?? "" },
                { "notes", cond.Notes ?? "" },
            });

            // 10. Accessories
            var acc = accessories ?? d.Accessories ?? new AccessoryDetails();
            root.Add("accessories", new JsonObject
            {
                { "charger_included", (object)acc.ChargerIncluded },
                { "charger_manufacturer", acc.ChargerManufacturer ?? "" },
                { "charger_model", acc.ChargerModel ?? "" },
                { "charger_wattage", (object)acc.ChargerWattage },
                { "charger_voltage", (object)acc.ChargerVoltage },
                { "charger_amperage", (object)acc.ChargerAmperage },
                { "connector_type", acc.ConnectorType ?? "" },
                { "bag_included", (object)acc.BagIncluded },
                { "stylus_included", (object)acc.StylusIncluded },
                { "dongle_included", (object)acc.DongleIncluded },
                { "notes", acc.Notes ?? "" },
            });

            // 11. Individual QC Tests
            if (testItems != null)
            {
                root.Add("tests", testItems.Select(t => new JsonObject
                {
                    { "test_id", t.Kind.ToString() },
                    { "name", t.Name },
                    { "status", t.StateLabel },
                    { "required", t.Required },
                    { "mandatory", t.Mandatory },
                    { "notes", t.Notes ?? "" },
                    { "completed_at", t.CompletedAt.HasValue ? t.CompletedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null },
                }).ToList());
            }

            // 12. Field Qualities
            if (d.FieldQualities != null && d.FieldQualities.Count > 0)
            {
                root.Add("field_qualities", d.FieldQualities.Select(q => new JsonObject
                {
                    { "field", q.FieldName },
                    { "status", q.Status },
                    { "source", q.Source },
                    { "reason", q.Reason },
                }).ToList());
            }

            // 13. Backwards-compatible "details" block
            root.Add("details", new JsonObject
            {
                { "os", d.System?.OperatingSystem ?? "" },
                { "bios_version", d.System?.BiosVersion ?? "" },
                { "product_code", d.System?.ProductCode ?? "" },
                { "warnings", d.Warnings ?? new List<string>() }
            });

            return JsonWriter.Serialize(root);
        }
    }
}
