using BBTech.Core.Models;
using BBTech.Core.Storage;

namespace BBTech.Core.Identity
{
    /// <summary>Builds identity/specs from the same DetectionResult that DETECT produces (one detection system).</summary>
    public static class IdentityFactory
    {
        public static DeviceIdentity From(DetectionResult r) =>
            new DeviceIdentity(r.Brand, r.Model, r.ModelNumber, r.System?.SerialNumber, r.System?.SystemUuid, r.System?.BoardSerial);

        public static DeviceSpecs SpecsFrom(DetectionResult r) => new DeviceSpecs
        {
            Cpu = r.Cpu, Generation = r.Generation, Ram = r.Ram, Storage = r.Storage, BatteryHealth = r.BatteryHealth,
        };
    }
}
