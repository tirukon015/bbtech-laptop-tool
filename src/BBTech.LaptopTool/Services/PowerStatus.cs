using System.Runtime.InteropServices;

namespace BBTech.LaptopTool.Services
{
    /// <summary>Live AC/battery state from GetSystemPowerStatus (no admin, no WMI).</summary>
    public sealed class PowerStatus
    {
        public bool? AcOnline { get; private set; }
        public bool NoSystemBattery { get; private set; }
        public bool Charging { get; private set; }
        public int? ChargePercent { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public static PowerStatus Read()
        {
            var ps = new PowerStatus();
            if (!GetSystemPowerStatus(out var s)) return ps;
            ps.AcOnline = s.ACLineStatus == 1 ? true : s.ACLineStatus == 0 ? false : (bool?)null;
            ps.NoSystemBattery = s.BatteryFlag != 255 && (s.BatteryFlag & 128) != 0;
            ps.Charging = s.BatteryFlag != 255 && (s.BatteryFlag & 8) != 0;
            ps.ChargePercent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : (int?)null;
            return ps;
        }
    }
}
