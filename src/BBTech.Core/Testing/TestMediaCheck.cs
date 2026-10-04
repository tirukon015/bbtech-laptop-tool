using System;
using System.IO;

namespace BBTech.Core.Testing
{
    public enum TestMediaState { Available, Missing, Corrupt }

    /// <summary>
    /// Validates the fixed BBTech speaker test media before it is handed to the player, so a
    /// missing or damaged file shows a friendly message instead of failing in the player.
    /// </summary>
    public static class TestMediaCheck
    {
        public const string UnavailableMessage = "BBTech test media is unavailable.";

        /// <summary>Smallest plausible size for the test clip (anything smaller is truncated).</summary>
        public const long MinimumBytes = 64 * 1024;

        public static TestMediaState Check(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return TestMediaState.Missing;
                var info = new FileInfo(path);
                if (info.Length < MinimumBytes) return TestMediaState.Corrupt;

                // MP4/MOV files start with an "ftyp" box: [size:4]["ftyp"].
                var header = new byte[12];
                using (var f = File.OpenRead(path))
                {
                    if (f.Read(header, 0, header.Length) < header.Length) return TestMediaState.Corrupt;
                }
                bool ftyp = header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p';
                return ftyp ? TestMediaState.Available : TestMediaState.Corrupt;
            }
            catch (Exception)
            {
                return TestMediaState.Corrupt; // unreadable (locked, permissions, I/O error)
            }
        }

        public static string Describe(TestMediaState state) =>
            state == TestMediaState.Available ? "" : UnavailableMessage;
    }
}
