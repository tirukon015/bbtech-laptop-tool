using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Audio
{
    /// <summary>
    /// Captures audio from the default recording device with the classic waveIn API
    /// (available on every Windows version, no extra DLLs). Reports a live peak level
    /// and can record a short clip for playback.
    /// </summary>
    public sealed class MicrophoneRecorder : IDisposable
    {
        public const int SampleRate = 16000;
        private const int BufferCount = 4;
        private const int BufferBytes = SampleRate * 2 / 10; // 100 ms mono 16-bit

        private IntPtr _handle;
        private readonly IntPtr[] _headers = new IntPtr[BufferCount];
        private readonly IntPtr[] _data = new IntPtr[BufferCount];
        private AutoResetEvent _event;
        private Thread _thread;
        private volatile bool _running;
        private readonly object _recSync = new object();
        private MemoryStream _recording;
        private int _recordBytesLeft;

        /// <summary>Peak level 0..1 for each 100 ms block (raised on a background thread).</summary>
        public event Action<double> Level;
        /// <summary>Raised (background thread) when a timed recording finishes, with WAV bytes.</summary>
        public event Action<byte[]> RecordingFinished;

        /// <summary>Returns null on success, otherwise a friendly error message.</summary>
        public string Start()
        {
            if (_running) return null;
            if (waveInGetNumDevs() == 0) return "Microphone not available on this laptop (no recording device found).";

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16,
                nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2, cbSize = 0,
            };
            _event = new AutoResetEvent(false);
            int rc = waveInOpen(out _handle, WAVE_MAPPER, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (rc != 0)
            {
                AppLog.Warn("waveInOpen failed: " + rc);
                _handle = IntPtr.Zero;
                return rc == 4 ? "The microphone is in use by another application." : "The microphone could not be opened (code " + rc + ").";
            }

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(BufferBytes);
                _headers[i] = Marshal.AllocHGlobal(hdrSize);
                var h = new WAVEHDR { lpData = _data[i], dwBufferLength = BufferBytes };
                Marshal.StructureToPtr(h, _headers[i], false);
                waveInPrepareHeader(_handle, _headers[i], hdrSize);
                waveInAddBuffer(_handle, _headers[i], hdrSize);
            }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MicCapture" };
            _thread.Start();
            rc = waveInStart(_handle);
            if (rc != 0)
            {
                Stop();
                return "The microphone could not be started (code " + rc + ").";
            }
            AppLog.Info("Microphone capture started");
            return null;
        }

        public void RecordSeconds(int seconds)
        {
            lock (_recSync)
            {
                _recording = new MemoryStream();
                _recordBytesLeft = seconds * SampleRate * 2;
            }
        }

        private void Loop()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            var buffer = new byte[BufferBytes];
            while (_running)
            {
                _event.WaitOne(200);
                for (int i = 0; i < BufferCount && _running; i++)
                {
                    var h = (WAVEHDR)Marshal.PtrToStructure(_headers[i], typeof(WAVEHDR));
                    if ((h.dwFlags & WHDR_DONE) == 0) continue;

                    int n = (int)h.dwBytesRecorded;
                    if (n > 0)
                    {
                        Marshal.Copy(h.lpData, buffer, 0, n);
                        Process(buffer, n);
                    }
                    h.dwFlags &= ~WHDR_DONE;
                    h.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(h, _headers[i], false);
                    if (_running) waveInAddBuffer(_handle, _headers[i], hdrSize);
                }
            }
        }

        private void Process(byte[] buf, int n)
        {
            int peak = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                int s = Math.Abs((int)BitConverter.ToInt16(buf, i));
                if (s > peak) peak = s;
            }
            try { Level?.Invoke(peak / 32768.0); } catch { }

            byte[] finished = null;
            lock (_recSync)
            {
                if (_recording != null && _recordBytesLeft > 0)
                {
                    int take = Math.Min(n, _recordBytesLeft);
                    _recording.Write(buf, 0, take);
                    _recordBytesLeft -= take;
                    if (_recordBytesLeft <= 0)
                    {
                        finished = WavBuilder.FromPcm16(_recording.ToArray(), 1, SampleRate);
                        _recording = null;
                    }
                }
            }
            if (finished != null) try { RecordingFinished?.Invoke(finished); } catch { }
        }

        public void Stop()
        {
            if (_handle == IntPtr.Zero) return;
            _running = false;
            try
            {
                waveInReset(_handle);
                _thread?.Join(1000);
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_headers[i] != IntPtr.Zero) waveInUnprepareHeader(_handle, _headers[i], hdrSize);
                }
                waveInClose(_handle);
            }
            catch (Exception ex) { AppLog.Warn("Microphone stop error", ex); }
            finally
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_headers[i]); _headers[i] = IntPtr.Zero; }
                    if (_data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_data[i]); _data[i] = IntPtr.Zero; }
                }
                _handle = IntPtr.Zero;
                _event?.Dispose();
                _event = null;
            }
        }

        public void Dispose() => Stop();

        // ---- winmm interop ----
        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_EVENT = 0x50000;
        private const uint WHDR_DONE = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public short wFormatTag; public short nChannels; public int nSamplesPerSec; public int nAvgBytesPerSec;
            public short nBlockAlign; public short wBitsPerSample; public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded; public IntPtr dwUser;
            public uint dwFlags; public uint dwLoops; public IntPtr lpNext; public IntPtr reserved;
        }

        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hWaveIn);
    }
}

