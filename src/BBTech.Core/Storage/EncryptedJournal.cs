using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BBTech.Core.Security;

namespace BBTech.Core.Storage
{
    public sealed class JournalReadStats
    {
        public int Valid { get; set; }
        /// <summary>Complete records that failed authentication (tampering/corruption) and were skipped.</summary>
        public int Corrupted { get; set; }
        /// <summary>Bytes of an incomplete final record (power loss / crash during a write) that were cut off.</summary>
        public long TruncatedTailBytes { get; set; }
    }

    /// <summary>
    /// Append-only encrypted journal (qc.journal). Every change is a new authenticated, encrypted
    /// record; nothing is ever overwritten, which preserves complete history and makes crashes safe:
    ///   • each append is flushed to disk before returning;
    ///   • an incomplete final record (power loss) is detected and cut off on the next open;
    ///   • a damaged record fails its HMAC and is skipped; later records are still read.
    ///
    /// File: "BBQCJ001" header, then records: ["BQR1"][int32 length][encrypted payload].
    /// </summary>
    public sealed class EncryptedJournal
    {
        public const string FileName = "qc.journal";
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("BBQCJ001");
        private static readonly byte[] RecordMagic = Encoding.ASCII.GetBytes("BQR1");
        private const int MaxRecord = 16 * 1024 * 1024;

        private readonly string _path;
        private readonly EnvelopeCrypto _crypto;
        private readonly object _sync = new object();

        public EncryptedJournal(string path, EnvelopeCrypto crypto)
        {
            _path = path;
            _crypto = crypto;
        }

        public static bool HasData(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > Header.Length; }
            catch { return false; }
        }

        /// <summary>Reads every readable record and repairs an incomplete tail.</summary>
        public List<byte[]> ReadAll(out JournalReadStats stats)
        {
            stats = new JournalReadStats();
            var records = new List<byte[]>();
            lock (_sync)
            {
                if (!File.Exists(_path)) return records;
                byte[] data = File.ReadAllBytes(_path);
                int pos = 0;
                if (data.Length >= Header.Length && StartsWith(data, 0, Header)) pos = Header.Length;
                long goodEnd = pos;

                while (pos < data.Length)
                {
                    if (!StartsWith(data, pos, RecordMagic))
                    {
                        int next = Find(data, RecordMagic, pos + 1);
                        if (next < 0) break;
                        pos = next;
                        continue;
                    }
                    if (pos + 8 > data.Length) break; // partial header
                    int len = BitConverter.ToInt32(data, pos + 4);
                    if (len <= 0 || len > MaxRecord) { pos += 4; continue; }
                    if (pos + 8 + len > data.Length) break; // partial record (crash during write)

                    var payload = new byte[len];
                    Buffer.BlockCopy(data, pos + 8, payload, 0, len);
                    byte[] plain = _crypto.TryDecrypt(payload);
                    if (plain != null)
                    {
                        records.Add(plain);
                        stats.Valid++;
                        pos += 8 + len;
                        goodEnd = Math.Max(goodEnd, pos);
                    }
                    else
                    {
                        // Damaged record: its length field may be wrong too, so resynchronise on the next
                        // record marker instead of trusting it. Complete bytes are kept (never truncated).
                        stats.Corrupted++;
                        goodEnd = Math.Max(goodEnd, pos + 8 + len);
                        int next = Find(data, RecordMagic, pos + 4);
                        if (next < 0) break;
                        pos = next;
                    }
                }

                if (goodEnd < data.Length)
                {
                    stats.TruncatedTailBytes = data.Length - goodEnd;
                    using (var f = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read)) f.SetLength(goodEnd);
                }
            }
            return records;
        }

        /// <summary>Encrypts and appends one record, flushed to disk before returning.</summary>
        public void Append(byte[] plain)
        {
            byte[] payload = _crypto.Encrypt(plain);
            lock (_sync)
            {
                bool isNew = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                using (var f = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    if (isNew) f.Write(Header, 0, Header.Length);
                    f.Write(RecordMagic, 0, RecordMagic.Length);
                    f.Write(BitConverter.GetBytes(payload.Length), 0, 4);
                    f.Write(payload, 0, payload.Length);
                    f.Flush(true);
                }
            }
        }

        private static bool StartsWith(byte[] data, int pos, byte[] pattern)
        {
            if (pos + pattern.Length > data.Length) return false;
            for (int i = 0; i < pattern.Length; i++) if (data[pos + i] != pattern[i]) return false;
            return true;
        }

        private static int Find(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
                if (StartsWith(data, i, pattern)) return i;
            return -1;
        }
    }
}
