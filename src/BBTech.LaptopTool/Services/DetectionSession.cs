using System.Threading.Tasks;
using BBTech.Core.Models;
using BBTech.LaptopTool.Detect;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Holds the current laptop's <see cref="DetectionResult"/> for this session.
    /// DETECT stores its result here; TEST reuses it for the report name and header.
    /// If DETECT was not run, the same <see cref="HardwareDetector"/> runs once on demand
    /// (only when a report is saved), so there is one detection system, not two.
    /// </summary>
    public static class DetectionSession
    {
        private static Task<DetectionResult> _pending;

        public static DetectionResult Current { get; private set; }

        public static void Set(DetectionResult result) => Current = result;

        public static async Task<DetectionResult> GetOrDetectAsync()
        {
            if (Current != null) return Current;
            if (_pending == null) _pending = HardwareDetector.DetectAsync(null);
            var r = await _pending;
            _pending = null;
            return Current = r;
        }
    }
}
