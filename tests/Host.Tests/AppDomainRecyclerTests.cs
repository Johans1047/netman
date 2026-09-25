using System;
using System.IO;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="AppDomainRecycler"/> — web.config touch with
    /// retry on locked targets. Uses short injected retry delays so the
    /// suite stays fast.
    /// </summary>
    public class AppDomainRecyclerTests
    {
        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hotreload-recycler-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Recycle_Success_AdvancesLastWriteAndKeepsContentBytes()
        {
            string root = CreateTempDirectory();
            try
            {
                string target = Path.Combine(root, "web.config");
                byte[] content = new byte[] { 0x3C, 0x3F, 0x78, 0x6D, 0x6C, 0x20, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x3D, 0x22, 0x31, 0x2E, 0x30, 0x22, 0x3F, 0x3E };
                File.WriteAllBytes(target, content);

                // Pin the timestamp in the past so "advanced" is unambiguous.
                DateTime previousWrite = DateTime.UtcNow.AddMinutes(-5);
                File.SetLastWriteTimeUtc(target, previousWrite);
                byte[] bytesBefore = File.ReadAllBytes(target);

                var recycler = new AppDomainRecycler(maxAttempts: 3, retryDelayMilliseconds: 10);
                RecycleResult result = recycler.Recycle(target);

                Assert.True(result.Success, "Recycle should succeed. Error: " + result.Error);
                Assert.Equal(1, result.Attempts);
                Assert.Equal(target, result.TargetPath);

                // Content must remain byte-identical — only the timestamp moves.
                byte[] bytesAfter = File.ReadAllBytes(target);
                Assert.Equal(bytesBefore, bytesAfter);

                DateTime writeAfter = File.GetLastWriteTimeUtc(target);
                Assert.True(writeAfter > previousWrite,
                    "LastWrite should advance. Before: " + previousWrite.ToString("O") +
                    ", after: " + writeAfter.ToString("O"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Recycle_MissingTarget_ReturnsStructuredFailure()
        {
            string root = CreateTempDirectory();
            try
            {
                string missing = Path.Combine(root, "missing-web.config");

                var recycler = new AppDomainRecycler(maxAttempts: 3, retryDelayMilliseconds: 10);
                RecycleResult result = recycler.Recycle(missing);

                Assert.False(result.Success, "Recycle against a missing target must fail structurally.");
                Assert.Contains("not found", result.Error);
                Assert.Equal(0, result.Attempts);
                Assert.Null(result.TargetPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Recycle_TargetLocked_RetriesThenReturnsStructuredFailure()
        {
            string root = CreateTempDirectory();
            try
            {
                string target = Path.Combine(root, "web.config");
                byte[] content = new byte[] { 0x3C, 0x2F, 0x63, 0x6F, 0x6E, 0x66, 0x69, 0x67, 0x75, 0x72, 0x61, 0x74, 0x69, 0x6F, 0x6E, 0x3E };
                File.WriteAllBytes(target, content);
                byte[] bytesBefore = File.ReadAllBytes(target);

                var recycler = new AppDomainRecycler(maxAttempts: 3, retryDelayMilliseconds: 10);
                RecycleResult result;

                // Lock the target with FileShare.None — SetLastWriteTimeUtc must
                // fail with a sharing violation (IOException) on every attempt.
                using (var lockStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    result = recycler.Recycle(target);
                }

                Assert.False(result.Success, "Recycle against a locked target must fail.");
                Assert.False(string.IsNullOrWhiteSpace(result.Error), "Structured failure must carry an error message.");
                Assert.Equal(3, result.Attempts);
                Assert.Null(result.TargetPath);

                // Content must be untouched by the failed touches.
                Assert.Equal(bytesBefore, File.ReadAllBytes(target));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
