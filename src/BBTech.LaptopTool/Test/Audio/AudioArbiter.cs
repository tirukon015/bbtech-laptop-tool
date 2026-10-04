using System;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Audio
{
    /// <summary>
    /// Only one audio source plays at a time. When a test sound (tones, sweep, microphone playback)
    /// or the BBTech test media starts, whatever was playing before is stopped first, so sounds
    /// never overlap and nothing keeps playing in the background. UI thread only.
    /// </summary>
    public static class AudioArbiter
    {
        private static string _owner;
        private static Action _stop;

        public static string Owner => _owner;

        public static void Acquire(string owner, Action stop)
        {
            if (_owner != null && _owner != owner)
            {
                var previous = _stop;
                string previousOwner = _owner;
                _owner = null;
                _stop = null;
                try { previous?.Invoke(); }
                catch (Exception ex) { AppLog.Warn("Could not stop audio owner " + previousOwner, ex); }
            }
            _owner = owner;
            _stop = stop;
        }

        public static void Release(string owner)
        {
            if (_owner != owner) return;
            _owner = null;
            _stop = null;
        }
    }
}
