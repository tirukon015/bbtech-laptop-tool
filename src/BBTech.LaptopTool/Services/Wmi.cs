using System;
using System.Collections.Generic;
using System.Management;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Services
{
    /// <summary>Read-only WMI queries that never throw.</summary>
    public static class Wmi
    {
        public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2", int timeoutSeconds = 5)
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql)))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
                    foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
                }
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                AppLog.Warn("WMI access denied: " + wql);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotSupported || ex.ErrorCode == ManagementStatus.InvalidClass || ex.ErrorCode == ManagementStatus.InvalidNamespace)
            {
                AppLog.Warn("WMI not supported here: " + wql);
            }
            catch (Exception ex)
            {
                AppLog.Warn("WMI query failed: " + wql, ex);
            }
            return list;
        }

        public static string Str(ManagementBaseObject o, string prop)
        {
            try { return (o[prop] ?? "").ToString().Trim(); }
            catch { return ""; } // property missing on older Windows versions
        }

        public static long Long(ManagementBaseObject o, string prop)
        {
            try { var v = o[prop]; return v == null ? 0 : Convert.ToInt64(v); }
            catch { return 0; }
        }

        public static int Int(ManagementBaseObject o, string prop)
        {
            try { var v = o[prop]; return v == null ? 0 : Convert.ToInt32(v); }
            catch { return 0; }
        }

        public static object Raw(ManagementBaseObject o, string prop)
        {
            try { return o[prop]; } catch { return null; }
        }
    }
}
