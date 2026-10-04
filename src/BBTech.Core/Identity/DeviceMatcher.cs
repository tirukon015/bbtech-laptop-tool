using System.Collections.Generic;
using System.Linq;
using BBTech.Core.Storage;

namespace BBTech.Core.Identity
{
    public enum MatchStatus
    {
        /// <summary>Exactly one stored device agrees on at least one stable identifier and none conflict.</summary>
        Matched,
        /// <summary>No stored device shares any stable identifier with this laptop.</summary>
        NotFound,
        /// <summary>Identifiers disagree, or several devices match: history must NOT be shown automatically.</summary>
        Conflict,
        /// <summary>This laptop reports no usable serial/UUID/board serial, so it cannot be matched safely.</summary>
        NoIdentifiers,
    }

    public sealed class MatchResult
    {
        public MatchStatus Status { get; set; }
        public DeviceRecord Device { get; set; }
        public List<DeviceRecord> Candidates { get; set; } = new List<DeviceRecord>();
        /// <summary>Plain-language explanation (shown to staff for Conflict / NoIdentifiers).</summary>
        public string Reason { get; set; } = "";
        /// <summary>Which identifiers agreed, e.g. "serial, uuid".</summary>
        public string MatchedOn { get; set; } = "";
    }

    /// <summary>
    /// Finds the stored record of the physical laptop. Rules:
    ///  • Only stable identifiers count: serial, system UUID, board serial (as hashes).
    ///    Model, CPU, RAM or storage never identify a laptop.
    ///  • A candidate must agree on at least one identifier.
    ///  • Any identifier present on both sides that DIFFERS (or a different manufacturer) is a conflict.
    ///  • More than one candidate is a conflict.
    ///  • Missing identifiers on either side are tolerated (not a conflict).
    /// A conflict is never resolved silently: the caller must show a warning instead of history.
    /// </summary>
    public static class DeviceMatcher
    {
        public static MatchResult Match(DeviceIdentity id, IEnumerable<DeviceRecord> devices)
        {
            if (id == null || !id.HasStrongIdentifier)
                return new MatchResult
                {
                    Status = MatchStatus.NoIdentifiers,
                    Reason = "This laptop does not report a serial number, system UUID or motherboard serial, so its QC history cannot be matched safely.",
                };

            var candidates = new List<(DeviceRecord Device, List<string> Agree, List<string> Differ)>();
            foreach (var d in devices ?? Enumerable.Empty<DeviceRecord>())
            {
                var agree = new List<string>();
                var differ = new List<string>();
                Compare("serial number", id.SerialHash, d.SerialHash, agree, differ);
                Compare("system UUID", id.UuidHash, d.UuidHash, agree, differ);
                Compare("motherboard serial", id.BoardSerialHash, d.BoardSerialHash, agree, differ);
                if (agree.Count == 0) continue;

                string m1 = DeviceIdentity.NormalizeName(id.Manufacturer), m2 = DeviceIdentity.NormalizeName(d.Manufacturer);
                if (m1.Length > 0 && m2.Length > 0 && m1 != m2) differ.Add("manufacturer");
                candidates.Add((d, agree, differ));
            }

            if (candidates.Count == 0)
                return new MatchResult { Status = MatchStatus.NotFound, Reason = "No QC inspection found for this device." };

            if (candidates.Count > 1)
                return new MatchResult
                {
                    Status = MatchStatus.Conflict,
                    Candidates = candidates.Select(c => c.Device).ToList(),
                    Reason = "This laptop's identifiers match " + candidates.Count + " different device records. History is not shown to avoid mixing laptops.",
                };

            var only = candidates[0];
            if (only.Differ.Count > 0)
                return new MatchResult
                {
                    Status = MatchStatus.Conflict,
                    Candidates = new List<DeviceRecord> { only.Device },
                    MatchedOn = string.Join(", ", only.Agree),
                    Reason = "Identity conflict: the " + string.Join(", ", only.Agree) + " matches a stored laptop, but the " +
                             string.Join(", ", only.Differ) + " is different (e.g. motherboard replaced, or a cloned disk). History is not shown automatically.",
                };

            return new MatchResult { Status = MatchStatus.Matched, Device = only.Device, MatchedOn = string.Join(", ", only.Agree) };
        }

        private static void Compare(string name, string current, string stored, List<string> agree, List<string> differ)
        {
            if (current == null || stored == null) return;
            if (current == stored) agree.Add(name); else differ.Add(name);
        }
    }
}
