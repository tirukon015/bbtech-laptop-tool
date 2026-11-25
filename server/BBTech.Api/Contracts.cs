using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBTech.Api;

// Wire contract shared with the desktop tool (BBTech.Core.Sync / Settings / Software / Testing DataContracts).
// Property names are PascalCase exactly as the client's DataContract names; enums are numbers; dates use
// the round-trip "o" format (the client parses dates with that exact format). See docs/API.md.

public sealed class OperatorDto
{
    public string OperatorId { get; set; }
    public string UserId { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }
    public bool Active { get; set; }
    public List<string> Departments { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SoftwarePackageDto
{
    public string PackageId { get; set; }
    public string Name { get; set; }
    public string Version { get; set; }
    public int SourceMode { get; set; }
    public string OnlineInstallerName { get; set; } = "";
    public string OnlineDownloadUrl { get; set; } = "";
    public string OfflineFolderPath { get; set; } = "";
    public string OfflineExecutableName { get; set; } = "";
    public string Sha256Checksum { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallArguments { get; set; } = "";
    public bool RequiresElevation { get; set; } = true;
    public bool Required { get; set; }
    public string DetectRule { get; set; } = "office";
    public int TimeoutMinutes { get; set; } = 60;
    public bool Approved { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TestRequirementDto
{
    public string TestId { get; set; }
    public string Department { get; set; }
    public string Label { get; set; }
    public bool Required { get; set; }
    public bool SkipAllowed { get; set; }
}

public sealed class QcRequirementsDto
{
    public int Version { get; set; }
    public List<TestRequirementDto> Tests { get; set; } = new();
    public bool AllowOutOfOrderWork { get; set; }
}

public sealed class LaptopModelDto
{
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public List<string> RequiredPackageIds { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class MasterConfigDto
{
    public int ConfigVersion { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OperatorDto> Operators { get; set; } = new();
    public List<SoftwarePackageDto> Packages { get; set; } = new();
    public QcRequirementsDto Requirements { get; set; }
    public List<LaptopModelDto> LaptopModels { get; set; } = new();
    public List<string> SupportedWindowsVersions { get; set; } = new();
}

public sealed class SyncQueueItemDto
{
    public string RecordId { get; set; }
    public string DeviceId { get; set; }
    public string OperatorId { get; set; }
    public string OperatorName { get; set; }
    public string Department { get; set; }
    public string TestId { get; set; }
    public string TestName { get; set; }
    public string Result { get; set; }
    public string Notes { get; set; }
    public DateTime Timestamp { get; set; }
    public string AppVersion { get; set; }
    public string Checksum { get; set; }
    public int State { get; set; }
    public string ServerReceipt { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; }
    public string DeviceSerial { get; set; }
    public string DeviceManufacturer { get; set; }
    public string DeviceModel { get; set; }
    public string Details { get; set; }
}

public sealed class AuditRecordDto
{
    public string AuditId { get; set; }
    public DateTime Timestamp { get; set; }
    public string ActorUserId { get; set; }
    public string ActorName { get; set; }
    public string Action { get; set; }
    public string Target { get; set; }
    public string OldValue { get; set; }
    public string NewValue { get; set; }
    public string Details { get; set; }
    public string PreviousChecksum { get; set; }
    public string Checksum { get; set; }
    public bool Synced { get; set; }
    /// <summary>Server only: the actor was confirmed (active user, allowed on the workstation, plausible time, client action).</summary>
    public bool ActorVerified { get; set; }
    /// <summary>Server only: "client" or "server".</summary>
    public string Source { get; set; }
}

public sealed class SyncUploadBatchDto
{
    public string BatchId { get; set; }
    public string DeviceId { get; set; }
    public DateTime SentAt { get; set; }
    public List<SyncQueueItemDto> Records { get; set; } = new();
    public List<AuditRecordDto> AuditRecords { get; set; } = new();
}

public sealed class RejectedRecordDto
{
    public string RecordId { get; set; }
    public string Reason { get; set; }
}

public sealed class SyncUploadResultDto
{
    public bool Success { get; set; }
    public string ServerReceiptId { get; set; }
    public int ProcessedCount { get; set; }
    public int DuplicateCount { get; set; }
    public List<string> VerifiedRecordIds { get; set; } = new();
    public List<string> VerifiedAuditIds { get; set; } = new();
    public List<RejectedRecordDto> Rejected { get; set; } = new();
    public string ErrorMessage { get; set; }
}

public sealed class DeviceStatusDto
{
    public string DeviceId { get; set; }
    /// <summary>READY (latest final QC = PASS), NOT_READY (latest = FAIL), IN_PROGRESS (results, no decision), UNKNOWN.</summary>
    public string Readiness { get; set; }
    public string QcDecision { get; set; }
    public string QcDecidedBy { get; set; }
    public DateTime? QcDecidedAt { get; set; }
    /// <summary>Lifecycle status (RECEIVED ... READY_FOR_ECOMMERCE ... READY_FOR_DELIVERY) from the server's own history.</summary>
    public string LifecycleStatus { get; set; }
    public int QcReturnCount { get; set; }
}

public sealed class TestResultDto
{
    public string RecordId { get; set; }
    public string DeviceId { get; set; }
    public string OperatorId { get; set; }
    public string OperatorName { get; set; }
    public string Department { get; set; }
    public string TestId { get; set; }
    public string TestName { get; set; }
    public string Status { get; set; }
    public string Notes { get; set; }
    public DateTime TestedAt { get; set; }
}

public sealed class DeviceHistoryDto
{
    public string RecordId { get; set; }
    public string DeviceId { get; set; }
    public string OperatorId { get; set; }
    public string OperatorName { get; set; }
    public string Department { get; set; }
    public string TestId { get; set; }
    public string TestName { get; set; }
    public string Status { get; set; }
    public string Notes { get; set; }
    public string Details { get; set; }
    public DateTime TestedAt { get; set; }
}

// ---- admin requests
public sealed class UserUpsertRequest
{
    public string UserId { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }
    public bool Active { get; set; } = true;
    public List<string> Departments { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}

public sealed class WorkstationRequest
{
    public string Name { get; set; }
    public bool IsAdmin { get; set; }
}

/// <summary>Dates always in the exact round-trip "o" format (the desktop client parses with that format).</summary>
public sealed class RoundTripDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue((value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("o", CultureInfo.InvariantCulture));
}

public static class ApiJson
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = null;               // PascalCase, as the client's DataContracts
        o.PropertyNameCaseInsensitive = true;
        o.Converters.Add(new RoundTripDateTimeConverter());
    }
}
