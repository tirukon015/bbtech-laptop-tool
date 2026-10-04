using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// The third-party utilities (BatteryInfoView, HD Sentinel, Free PC Audit) are embedded in
    /// "BBTech Laptop Tool.exe" as compressed resources, so the USB only needs the single EXE.
    ///
    /// A tool is extracted only when it is first used (lazy), into
    /// %TEMP%\BBTechLaptopTool\&lt;version&gt;\&lt;tool&gt;, and the whole folder is deleted when the app
    /// closes, so nothing is left behind on the customer's laptop and every run starts with fresh
    /// tool data (no previous laptop's HD Sentinel data).
    ///
    /// Override: if a folder "tools\&lt;tool&gt;" exists next to the EXE it is used instead (for example a
    /// licensed HD Sentinel Portable folder that must keep its licence file on the USB).
    /// </summary>
    public static class ToolBundle
    {
        public const string BatteryInfoView = "BatteryInfoView";
        public const string HdSentinel = "HDSentinel";
        public const string FreePcAudit = "FreePCAudit";
        public const string SpeakerVideo = "SpeakerTest.mp4";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ExtractRoot =>
            Path.Combine(Path.GetTempPath(), "BBTechLaptopTool", AppPaths.Version);

        private static string ResourceName(string tool) => "bundle." + tool + (tool.EndsWith(".mp4") ? "" : ".zip");

        private static string ExternalDir(string tool) => Path.Combine(AppPaths.BaseDir, "tools", tool);

        /// <summary>Cheap check (no extraction): is the tool embedded or provided next to the EXE?</summary>
        public static bool IsAvailable(string tool)
        {
            if (Directory.Exists(ExternalDir(tool))) return true;
            return Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName(tool)) != null;
        }

        /// <summary>
        /// Returns the folder containing the tool, extracting it on first use. Null if unavailable.
        /// Can take a few seconds for HD Sentinel: call from a background thread.
        /// </summary>
        public static string GetToolDir(string tool)
        {
            lock (Sync)
            {
                if (Resolved.TryGetValue(tool, out var cached) && Directory.Exists(cached)) return cached;

                string external = ExternalDir(tool);
                if (Directory.Exists(external)) return Resolved[tool] = external;

                string target = Path.Combine(ExtractRoot, tool);
                string marker = Path.Combine(target, ".complete");
                if (File.Exists(marker)) return Resolved[tool] = target;

                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName(tool)))
                {
                    if (res == null)
                    {
                        AppLog.Warn("Embedded tool not found: " + tool);
                        return null;
                    }
                    try
                    {
                        if (Directory.Exists(target))
                        {
                            // An incomplete earlier extraction may still be locked (e.g. the tool is open):
                            // use a fresh folder instead of failing.
                            try { Directory.Delete(target, true); }
                            catch (IOException) { target = target + "-" + Guid.NewGuid().ToString("N").Substring(0, 8); marker = Path.Combine(target, ".complete"); }
                            catch (UnauthorizedAccessException) { target = target + "-" + Guid.NewGuid().ToString("N").Substring(0, 8); marker = Path.Combine(target, ".complete"); }
                        }
                        Directory.CreateDirectory(target);
                        using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
                        {
                            string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
                            foreach (var entry in zip.Entries)
                            {
                                string dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                entry.ExtractToFile(dest, true);
                            }
                        }
                        File.WriteAllText(marker, DateTime.Now.ToString("s"));
                        AppLog.Info("Extracted embedded tool: " + tool);
                        return Resolved[tool] = target;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Could not extract " + tool, ex);
                        return null;
                    }
                }
            }
        }

        /// <summary>Embedded media file (optional), extracted on demand. Null if not embedded.</summary>
        public static string GetMediaFile(string name)
        {
            lock (Sync)
            {
                string external = Path.Combine(AppPaths.BaseDir, "tests", name);
                if (File.Exists(external)) return external;
                string target = Path.Combine(ExtractRoot, "media", name);
                if (File.Exists(target)) return target;
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName(name)))
                {
                    if (res == null) return null;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var f = File.Create(target)) res.CopyTo(f);
                        return target;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Could not extract media " + name, ex);
                        return null;
                    }
                }
            }
        }

        public static bool HasMedia(string name) =>
            File.Exists(Path.Combine(AppPaths.BaseDir, "tests", name)) ||
            Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName(name)) != null;

        /// <summary>Called on exit: remove everything extracted to %TEMP% (best effort).</summary>
        public static void Cleanup()
        {
            try
            {
                string all = Path.Combine(Path.GetTempPath(), "BBTechLaptopTool");
                if (!Directory.Exists(all)) return;
                foreach (var d in Directory.GetDirectories(all).Concat(new[] { all }))
                {
                    try { Directory.Delete(d, true); } catch { /* a tool (e.g. HD Sentinel) may still be open */ }
                }
            }
            catch { }
        }
    }
}
