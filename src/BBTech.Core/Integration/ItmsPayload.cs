using System;
using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Export;
using BBTech.Core.Identity;
using BBTech.Core.Models;
using BBTech.Core.Storage;

namespace BBTech.Core.Integration
{
    /// <summary>
    /// The semantic device payload for ITMS: what the Laptop Tool detected about ONE physical laptop.
    ///
    /// Identity: the serial number is the primary identity. System UUID, motherboard serial and the
    /// hardware fingerprint are supporting identity, so ITMS can recognise the same physical laptop
    /// (ITMS makes the duplicate decision, not the Laptop Tool). Identifiers are the cleaned values used
    /// for local device matching: firmware placeholders ("To be filled by O.E.M.", all-zero UUIDs) are
    /// never sent. The fingerprint is a SHA-256 over stable firmware identifiers only (see DeviceIdentity).
    ///
    /// Hardware values are the plain detected texts ("Dell", "Latitude", "5420", "i5", "11th Gen" ...).
    /// The payload deliberately carries NO ITMS reference ids (brand_id, model_id, cpu_id, source_id ...):
    /// ITMS resolves its own reference data. A value the tool could not detect is sent as null
    /// (never as the display text "Unknown"), so ITMS cannot mistake it for a real brand or model.
    ///
    /// This is an architectural contract. The final wire format is decided when the ITMS side is built.
    /// </summary>
    public sealed class ItmsDevicePayload
    {
        /// <summary>The JSON keys, in the agreed order.</summary>
        public static readonly string[] Keys =
        {
            "serial_number", "system_uuid", "board_serial", "hardware_fingerprint",
            "brand", "model", "model_number", "cpu", "generation", "ram", "storage", "battery_health",
        };

        public string SerialNumber { get; }
        public string SystemUuid { get; }
        public string BoardSerial { get; }
        public string HardwareFingerprint { get; }
        public string Brand { get; }
        public string Model { get; }
        public string ModelNumber { get; }
        public string Cpu { get; }
        public string Generation { get; }
        public string Ram { get; }
        public string Storage { get; }
        public string BatteryHealth { get; }

        public ItmsDevicePayload(string serialNumber, string systemUuid, string boardSerial, string hardwareFingerprint,
                                 string brand, string model, string modelNumber, string cpu,
                                 string generation, string ram, string storage, string batteryHealth)
        {
            SerialNumber = Value(serialNumber);
            SystemUuid = Value(systemUuid);
            BoardSerial = Value(boardSerial);
            HardwareFingerprint = Value(hardwareFingerprint);
            Brand = Value(brand);
            Model = Value(model);
            ModelNumber = Value(modelNumber);
            Cpu = Value(cpu);
            Generation = Value(generation);
            Ram = Value(ram);
            Storage = Value(storage);
            BatteryHealth = Value(batteryHealth);
        }

        /// <summary>From the live DETECT result of the laptop the tool is running on.</summary>
        public static ItmsDevicePayload From(DetectionResult r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var id = IdentityFactory.From(r);
            return new ItmsDevicePayload(id.SerialNumber, id.SystemUuid, id.BoardSerial, id.Fingerprint,
                                         r.Brand, r.Model, r.ModelNumber, r.Cpu, r.Generation, r.Ram, r.Storage, r.BatteryHealth);
        }

        /// <summary>From the stored device record (its identifiers and latest hardware summary).</summary>
        public static ItmsDevicePayload From(DeviceRecord d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            var s = d.Specs ?? new DeviceSpecs();
            return new ItmsDevicePayload(d.SerialNumber, d.SystemUuid, d.BoardSerial, d.HardwareFingerprint,
                                         d.Manufacturer, d.Model, d.ModelNumber, s.Cpu, s.Generation, s.Ram, s.Storage, s.BatteryHealth);
        }

        /// <summary>
        /// Problems that make the payload unusable. The serial number is required: it is the primary
        /// identity of the physical laptop. Other fields may be null; ITMS staff complete them there.
        /// </summary>
        public IReadOnlyList<string> Problems
        {
            get
            {
                var list = new List<string>();
                if (SerialNumber == null) list.Add("The serial number of this laptop could not be detected.");
                return list;
            }
        }

        public bool IsValid => Problems.Count == 0;

        public JsonObject ToJsonObject() => new JsonObject
        {
            { "serial_number", SerialNumber },
            { "system_uuid", SystemUuid },
            { "board_serial", BoardSerial },
            { "hardware_fingerprint", HardwareFingerprint },
            { "brand", Brand },
            { "model", Model },
            { "model_number", ModelNumber },
            { "cpu", Cpu },
            { "generation", Generation },
            { "ram", Ram },
            { "storage", Storage },
            { "battery_health", BatteryHealth },
        };

        public string ToJson(bool indent = true) => JsonWriter.Serialize(ToJsonObject(), indent);

        /// <summary>Trimmed text; null for empty values and the display placeholder "Unknown".</summary>
        private static string Value(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            return v.Equals(DetectionResult.Unknown, StringComparison.OrdinalIgnoreCase) ? null : v;
        }
    }

    /// <summary>
    /// One send operation for ONE laptop: <c>{ "request_id": "...", "device": { ... } }</c>.
    ///
    /// The request id makes the send idempotent: it is created once when the laptop is marked ready to
    /// send and REUSED for every retry of that send, so ITMS can answer "already processed" instead of
    /// queuing the laptop twice.
    /// </summary>
    public sealed class ItmsSubmission
    {
        public string RequestId { get; }
        public ItmsDevicePayload Device { get; }

        public ItmsSubmission(string requestId, ItmsDevicePayload device)
        {
            if (!Guid.TryParse(requestId, out _)) throw new ArgumentException("A request id must be a UUID.", nameof(requestId));
            Device = device ?? throw new ArgumentNullException(nameof(device));
            if (!device.IsValid) throw new ArgumentException(string.Join(" ", device.Problems), nameof(device));
            RequestId = requestId.Trim();
        }

        public static string NewRequestId() => Guid.NewGuid().ToString("D");

        public JsonObject ToJsonObject() => new JsonObject { { "request_id", RequestId }, { "device", Device.ToJsonObject() } };

        public string ToJson(bool indent = true) => JsonWriter.Serialize(ToJsonObject(), indent);
    }

    /// <summary>
    /// Several device payloads in one request: <c>{ "request_id": "...", "devices": [ ... ] }</c>.
    ///
    /// A FUTURE TRANSPORT CAPABILITY ONLY. The Laptop Tool's workflow is one physical laptop → one
    /// device payload → one send; laptops are never collected locally to be sent together, and no
    /// screen offers it. Batch review, bulk editing and registration of many laptops happen in ITMS
    /// (its Registration Queue).
    /// </summary>
    public sealed class ItmsBatchPayload
    {
        public string RequestId { get; }
        public IReadOnlyList<ItmsDevicePayload> Devices { get; }

        public ItmsBatchPayload(string requestId, IEnumerable<ItmsDevicePayload> devices)
        {
            if (!Guid.TryParse(requestId, out _)) throw new ArgumentException("A request id must be a UUID.", nameof(requestId));
            var list = (devices ?? throw new ArgumentNullException(nameof(devices))).ToList();
            if (list.Count == 0) throw new ArgumentException("A batch needs at least one device.", nameof(devices));
            if (list.Any(d => d == null)) throw new ArgumentException("A batch cannot contain an empty entry.", nameof(devices));
            if (list.Any(d => !d.IsValid)) throw new ArgumentException("Every device in a batch needs a serial number.", nameof(devices));
            if (list.GroupBy(d => d.SerialNumber, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new ArgumentException("The same laptop appears more than once in the batch.", nameof(devices));
            RequestId = requestId.Trim();
            Devices = list.AsReadOnly();
        }

        public JsonObject ToJsonObject() => new JsonObject
        {
            { "request_id", RequestId },
            { "devices", Devices.Select(d => d.ToJsonObject()).ToList() },
        };

        public string ToJson(bool indent = true) => JsonWriter.Serialize(ToJsonObject(), indent);
    }
}
