using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BBTech.LaptopTool.Services;

namespace BBTech.LaptopTool.Test.Usb
{
    /// <summary>
    /// Watches for USB device arrivals by polling the Plug and Play device list. Each arrival is
    /// real evidence that the port the device was plugged into works.
    /// </summary>
    public sealed class UsbMonitor : IDisposable
    {
        private Timer _timer;
        private HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _busy;

        /// <summary>Raised on a background thread with the friendliest name of the new device.</summary>
        public event Action<string> DeviceArrived;
        public event Action DeviceRemoved;

        public static List<string> Controllers() =>
            Wmi.Query("SELECT Name FROM Win32_USBController").Select(o => Wmi.Str(o, "Name")).Where(n => n.Length > 0).Distinct().ToList();

        /// <summary>Currently connected USB devices with friendly names (hubs and generic entries omitted).</summary>
        public static List<string> ConnectedDevices() =>
            Snapshot().Values.Where(n => !IsGeneric(n)).Distinct().OrderBy(n => n).ToList();

        public void Start()
        {
            _known = new HashSet<string>(Snapshot().Keys, StringComparer.OrdinalIgnoreCase);
            _timer = new Timer(_ => Poll(), null, 1500, 1500);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var now = Snapshot();
                var added = now.Where(kv => !_known.Contains(kv.Key)).ToList();
                bool removed = _known.Any(k => !now.ContainsKey(k));
                _known = new HashSet<string>(now.Keys, StringComparer.OrdinalIgnoreCase);

                if (added.Count > 0)
                {
                    string name = added.Select(a => a.Value).OrderBy(n => IsGeneric(n) ? 1 : 0).FirstOrDefault() ?? "USB device";
                    DeviceArrived?.Invoke(name);
                }
                else if (removed) DeviceRemoved?.Invoke();
            }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private static Dictionary<string, string> Snapshot()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in Wmi.Query("SELECT DeviceID, Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'"))
            {
                string id = Wmi.Str(o, "DeviceID");
                if (id.Length == 0 || id.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
                map[id] = Wmi.Str(o, "Name");
            }
            return map;
        }

        private static bool IsGeneric(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string n = name.ToLowerInvariant();
            return n.Contains("hub") || n.Contains("composite") || n == "usb input device" || n.Contains("usb mass storage") ||
                   n.Contains("host controller") || n.Contains("usb connector manager") || n.Contains("ucm-ucsi");
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
