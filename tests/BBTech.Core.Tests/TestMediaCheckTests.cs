using System;
using System.IO;
using BBTech.Core.Testing;
using Xunit;

namespace BBTech.Core.Tests
{
    public class TestMediaCheckTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbtech-media-tests-" + Guid.NewGuid().ToString("N"));

        public TestMediaCheckTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Write(string name, byte[] header, int totalBytes)
        {
            var data = new byte[totalBytes];
            Array.Copy(header, data, Math.Min(header.Length, totalBytes));
            string p = Path.Combine(_dir, name);
            File.WriteAllBytes(p, data);
            return p;
        }

        private static readonly byte[] Mp4Header = { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };

        [Fact]
        public void Valid_mp4_is_available() =>
            Assert.Equal(TestMediaState.Available, TestMediaCheck.Check(Write("ok.mp4", Mp4Header, 200 * 1024)));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void No_path_is_missing(string path) => Assert.Equal(TestMediaState.Missing, TestMediaCheck.Check(path));

        [Fact]
        public void Nonexistent_file_is_missing() =>
            Assert.Equal(TestMediaState.Missing, TestMediaCheck.Check(Path.Combine(_dir, "does-not-exist.mp4")));

        [Fact]
        public void Truncated_file_is_corrupt() =>
            Assert.Equal(TestMediaState.Corrupt, TestMediaCheck.Check(Write("short.mp4", Mp4Header, 1000)));

        [Fact]
        public void Empty_file_is_corrupt() =>
            Assert.Equal(TestMediaState.Corrupt, TestMediaCheck.Check(Write("empty.mp4", new byte[0], 0)));

        [Fact]
        public void Wrong_content_is_corrupt() =>
            Assert.Equal(TestMediaState.Corrupt, TestMediaCheck.Check(Write("text.mp4", System.Text.Encoding.ASCII.GetBytes("this is not a video file"), 200 * 1024)));

        [Fact]
        public void Locked_file_does_not_throw()
        {
            string p = Write("locked.mp4", Mp4Header, 200 * 1024);
            using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(TestMediaState.Corrupt, TestMediaCheck.Check(p));
        }

        [Fact]
        public void Unavailable_message_is_friendly()
        {
            Assert.Equal("BBTech test media is unavailable.", TestMediaCheck.Describe(TestMediaState.Missing));
            Assert.Equal("BBTech test media is unavailable.", TestMediaCheck.Describe(TestMediaState.Corrupt));
            Assert.Equal("", TestMediaCheck.Describe(TestMediaState.Available));
        }
    }
}
