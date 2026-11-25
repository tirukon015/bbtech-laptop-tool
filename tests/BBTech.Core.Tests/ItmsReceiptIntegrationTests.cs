using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBTech.Core.Integration;
using BBTech.Core.Models;
using BBTech.Core.Storage;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    /// <summary>
    /// Laptop Tool → (real HTTP) → reference ITMS endpoint → ITMS stores the traceability record → ITMS returns the SHA-256 of
    /// what it stored → Laptop Tool verifies it. The reference endpoint follows docs/ITMS-LAPTOP-TOOL-CONTRACT.md §3.4:
    /// it computes the hash itself (it never copies the client's value).
    /// </summary>
    public sealed class ItmsReceiptIntegrationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-itms-" + Guid.NewGuid().ToString("N"));
        public ItmsReceiptIntegrationTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        /// <summary>Minimal HTTP/1.1 server on 127.0.0.1 behaving like ITMS POST /api/devices/requests.</summary>
        private sealed class ReferenceItms : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            public readonly List<string> StoredTraceability = new List<string>();
            public Func<string, string> AlterBeforeStore = s => s;     // simulates an ITMS that stores something else
            public bool EchoHash = true;
            public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

            public ReferenceItms() { _listener.Start(); Task.Run(Loop); }

            private async Task Loop()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); } catch { return; }
                    using (c) using (var s = c.GetStream())
                    {
                        string head = ReadHead(s);
                        int len = 0;
                        foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(line.Substring(15).Trim());
                        var body = new byte[len]; int read = 0;
                        while (read < len) { int n = s.Read(body, read, len - read); if (n <= 0) break; read += n; }
                        string reply = Handle(Encoding.UTF8.GetString(body, 0, read));
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(header, 0, header.Length); s.Write(bytes, 0, bytes.Length);
                    }
                }
            }

            private static string ReadHead(Stream s)
            {
                var sb = new StringBuilder();
                while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) { int b = s.ReadByte(); if (b < 0) break; sb.Append((char)b); }
                return sb.ToString();
            }

            private string Handle(string body)
            {
                string stored = AlterBeforeStore(Canonical(ExtractValue(body, "bbtech_traceability")));
                StoredTraceability.Add(stored);
                string hash = Sha256Hex(stored);                       // hash of what ITMS actually stored
                return "{\"success\":true,\"status\":\"queued\",\"request_no\":" + (4000 + StoredTraceability.Count) +
                       (EchoHash ? ",\"traceability_sha256\":\"" + hash + "\"" : "") + "}";
            }

            public void Dispose() { _stop.Cancel(); _listener.Stop(); }
        }

        /// <summary>The raw JSON value of a top-level property (balanced braces, string-aware).</summary>
        internal static string ExtractValue(string json, string property)
        {
            int k = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int i = json.IndexOf(':', k) + 1;
            while (char.IsWhiteSpace(json[i])) i++;
            int depth = 0; bool inStr = false;
            for (int j = i; j < json.Length; j++)
            {
                char ch = json[j];
                if (inStr) { if (ch == '\\') j++; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') inStr = true;
                else if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') { if (--depth == 0) return json.Substring(i, j - i + 1); }
            }
            return null;
        }

        /// <summary>Removes whitespace outside strings: identical to JSON.stringify(JSON.parse(x)) for this payload.</summary>
        internal static string Canonical(string json)
        {
            var sb = new StringBuilder(json.Length); bool inStr = false;
            for (int j = 0; j < json.Length; j++)
            {
                char ch = json[j];
                if (inStr) { sb.Append(ch); if (ch == '\\') sb.Append(json[++j]); else if (ch == '"') inStr = false; continue; }
                if (ch == '"') { inStr = true; sb.Append(ch); }
                else if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(Encoding.UTF8.GetBytes(s))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private (QcRepository repo, DeviceRecord d) ReadyLaptop()
        {
            var open = QcRepository.Open(_dir, new FakeMachine(21), QcStoreTests.LaptopA);
            Assert.True(open.Ok, open.Message);
            var repo = open.Repository;
            var d = repo.RegisterDevice(QcStoreTests.LaptopA, new DeviceSpecs());
            void Rec(string dept, string id, string by, string notes = null, string details = null)
            { Thread.Sleep(12); repo.RecordTest(d.DeviceId, id, id, "PASS", by, dept, notes, details, operatorId: by.ToLowerInvariant()); }

            var inv = new TechnicalInventory { CapturedAt = DateTime.UtcNow, ToolVersion = "1.6.0", Stage = "QC_DECISION" };
            inv.Add("storage", "Micron MTFDKBA512TFK", "WMI", DataConfidence.Detected, "SSD-1").SetId("serial_number", "SSD-1")
               .SetFrom("raw.percentage_used", 4, "NVMe SMART / Health Information log (Windows IOCTL)", DataConfidence.Measured)
               .SetFrom("raw.composite_temperature_k", 318, "NVMe SMART / Health Information log (Windows IOCTL)", DataConfidence.Measured);
            inv.Add("cpu", "Intel Core i5-1145G7 °C ® test", "WMI", DataConfidence.Detected);     // non-ASCII must hash identically
            Rec(Departments.Hardware, LifecycleEvents.DeviceSnapshot, "Kamal", inv.HardwareSignature(), inv.ToJson());

            var req = QcRequirements.Default();
            foreach (var t in req.Required(Departments.Hardware)) Rec(Departments.Hardware, t, "Kamal");
            Rec(Departments.Hardware, LifecycleEvents.HardwareHandover, "Kamal");
            foreach (var t in req.Required(Departments.Software)) Rec(Departments.Software, t, "Shadir");
            Rec(Departments.Software, LifecycleEvents.SoftwareHandover, "Shadir");
            foreach (var t in req.Required(Departments.Qc)) Rec(Departments.Qc, t, "Jimmy");
            Rec(Departments.Qc, LifecycleEvents.QcDecision, "Jimmy");
            return (repo, d);
        }

        [Fact]
        public async Task LaptopTool_To_Itms_StoresTraceability_EchoesHash_IsVerified()
        {
            var (repo, d) = ReadyLaptop();
            using (var itms = new ReferenceItms())
            {
                var outcome = await new ItmsTransmissionService(repo, new HttpItmsConnector(() => itms.Url, () => "test-key")).SendOrContinueAsync(d);

                Assert.Equal("VERIFIED", outcome.TraceabilityVerification);
                Assert.True(outcome.Confirmed);
                var stored = Assert.Single(itms.StoredTraceability);
                Assert.Contains("\"percentage_used\":{\"value\":\"4\"", stored);                 // ITMS holds the measured values
                Assert.Contains("°C ®", stored);
                Assert.Contains("traceability VERIFIED", outcome.Record.RemoteId);              // the local receipt says so
            }
        }

        [Fact]
        public async Task ItmsStoringSomethingElse_IsMismatch_AndNeverCountsAsDelivered()
        {
            var (repo, d) = ReadyLaptop();
            using (var itms = new ReferenceItms { AlterBeforeStore = s => s.Replace("\"value\":\"4\"", "\"value\":\"0\"") })
            {
                var outcome = await new ItmsTransmissionService(repo, new HttpItmsConnector(() => itms.Url, () => "test-key")).SendOrContinueAsync(d);

                Assert.Equal("MISMATCH", outcome.TraceabilityVerification);
                Assert.False(outcome.Confirmed);
                Assert.Contains("hash mismatch", outcome.Record.LastError);
            }
        }

        [Fact]
        public async Task ItmsWithoutTheHashEcho_IsUnconfirmed()
        {
            var (repo, d) = ReadyLaptop();
            using (var itms = new ReferenceItms { EchoHash = false })
            {
                var outcome = await new ItmsTransmissionService(repo, new HttpItmsConnector(() => itms.Url, () => "test-key")).SendOrContinueAsync(d);
                Assert.Equal("UNCONFIRMED", outcome.TraceabilityVerification);
                Assert.Contains("traceability UNCONFIRMED", outcome.Record.RemoteId);
            }
        }
    }
}
