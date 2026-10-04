using System;
using System.IO;
using System.Media;
using BBTech.LaptopTool.Logging;

namespace BBTech.LaptopTool.Test.Audio
{
    public enum Channel { Left, Right, Both }

    /// <summary>Generates test tones in memory and plays them on the default output device.</summary>
    public static class AudioPlayback
    {
        private const int SampleRate = 44100;
        private static SoundPlayer _player;
        private static MemoryStream _stream;

        /// <summary>Three short beeps on the chosen channel(s). Left uses a lower pitch than right.</summary>
        public static void PlayTone(Channel channel)
        {
            double freq = channel == Channel.Left ? 523.25 : channel == Channel.Right ? 783.99 : 659.25; // C5 / G5 / E5
            short[] samples = BuildBeeps(freq, channel, beeps: 3, beepMs: 350, gapMs: 150);
            Play(WavBuilder.Build(samples, 2, SampleRate));
        }

        /// <summary>Rising sweep 200 Hz - 4 kHz on both channels: reveals rattles and distortion.</summary>
        public static void PlaySweep()
        {
            int n = SampleRate * 4;
            var data = new short[n * 2];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = 200 * Math.Pow(20, t);
                phase += 2 * Math.PI * f / SampleRate;
                short v = (short)(Math.Sin(phase) * 0.35 * short.MaxValue * Envelope(i, n));
                data[2 * i] = v;
                data[2 * i + 1] = v;
            }
            Play(WavBuilder.Build(data, 2, SampleRate));
        }

        public static void PlayWav(byte[] wav) => Play(wav);

        public static void Stop()
        {
            StopCore();
            AudioArbiter.Release("tones");
        }

        private static void StopCore()
        {
            try { _player?.Stop(); } catch { }
        }

        private static void Play(byte[] wav)
        {
            try
            {
                StopCore();
                AudioArbiter.Acquire("tones", StopCore); // stops the test media if it is playing
                _player?.Dispose();
                _stream?.Dispose();
                _stream = new MemoryStream(wav);
                _player = new SoundPlayer(_stream);
                _player.Play();
            }
            catch (Exception ex)
            {
                AppLog.Error("Audio playback failed", ex);
                throw new InvalidOperationException("Sound could not be played. Check that an audio output device is enabled.");
            }
        }

        private static short[] BuildBeeps(double freq, Channel ch, int beeps, int beepMs, int gapMs)
        {
            int beepLen = SampleRate * beepMs / 1000, gapLen = SampleRate * gapMs / 1000;
            int total = beeps * (beepLen + gapLen);
            var data = new short[total * 2];
            for (int b = 0; b < beeps; b++)
            {
                int start = b * (beepLen + gapLen);
                for (int i = 0; i < beepLen; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * freq * i / SampleRate) * 0.5 * short.MaxValue * Envelope(i, beepLen));
                    int idx = (start + i) * 2;
                    if (ch != Channel.Right) data[idx] = v;
                    if (ch != Channel.Left) data[idx + 1] = v;
                }
            }
            return data;
        }

        // 10 ms fade in/out to avoid clicks.
        private static double Envelope(int i, int n)
        {
            int fade = SampleRate / 100;
            if (i < fade) return (double)i / fade;
            if (i > n - fade) return Math.Max(0, (double)(n - i) / fade);
            return 1;
        }
    }

    public static class WavBuilder
    {
        /// <summary>16-bit PCM WAV from interleaved samples.</summary>
        public static byte[] Build(short[] samples, int channels, int sampleRate)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataBytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (short s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static byte[] FromPcm16(byte[] pcm, int channels, int sampleRate)
        {
            var samples = new short[pcm.Length / 2];
            Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
            return Build(samples, channels, sampleRate);
        }
    }
}
