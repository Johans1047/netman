using System;
using System.IO;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="ShadowCopyManager"/> — temp directory copy and cleanup.
    /// TDD RED→GREEN: tests verify copy/cleanup/retain behavior.
    /// </summary>
    public class ShadowCopyManagerTests
    {
        private static readonly string TestGuid = Guid.NewGuid().ToString("N");

        [Fact]
        public void CopyToTemp_ValidBuildOutput_CreatesShadowDirectory()
        {
            string buildDir = CreateTempBuildOutput();
            try
            {
                var manager = new ShadowCopyManager(TestGuid);
                string shadowPath = manager.CopyToTemp(buildDir);

                Assert.True(Directory.Exists(shadowPath), "Shadow directory should exist.");
                Assert.Contains("hotreload", shadowPath);

                string[] shadowFiles = Directory.GetFiles(shadowPath);
                Assert.True(shadowFiles.Length > 0, "Shadow directory should contain copied files.");
            }
            finally
            {
                Directory.Delete(buildDir, recursive: true);
            }
        }

        [Fact]
        public void CopyToTemp_NonExistentSource_ThrowsDirectoryNotFoundException()
        {
            var manager = new ShadowCopyManager(TestGuid);
            Assert.Throws<DirectoryNotFoundException>(
                () => manager.CopyToTemp(@"C:\nonexistent\build"));
        }

        [Fact]
        public void CleanupOldCopies_RetainsLastN()
        {
            string buildDir = CreateTempBuildOutput();
            try
            {
                var manager = new ShadowCopyManager(TestGuid, retainCount: 2);

                // Create 4 shadow copies
                for (int i = 0; i < 4; i++)
                {
                    manager.CopyToTemp(buildDir);
                    System.Threading.Thread.Sleep(1100); // ensure distinct timestamps
                }

                manager.CleanupOldCopies();

                string shadowRoot = Path.Combine(Path.GetTempPath(), "hotreload", TestGuid);
                string[] remaining = Directory.GetDirectories(shadowRoot);
                Assert.True(remaining.Length <= 2, "Should retain at most 2 copies.");
            }
            finally
            {
                Directory.Delete(buildDir, recursive: true);
            }
        }

        [Fact]
        public void GetLatestShadowCopy_ReturnsNewest()
        {
            string buildDir = CreateTempBuildOutput();
            try
            {
                var manager = new ShadowCopyManager(TestGuid, retainCount: 5);

                manager.CopyToTemp(buildDir);
                System.Threading.Thread.Sleep(1100);
                string latest = manager.CopyToTemp(buildDir);

                string result = manager.GetLatestShadowCopy();
                Assert.Equal(latest, result);
            }
            finally
            {
                Directory.Delete(buildDir, recursive: true);
            }
        }

        [Fact]
        public void Constructor_NullGuid_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new ShadowCopyManager(null));
        }

        private static string CreateTempBuildOutput()
        {
            string dir = Path.Combine(Path.GetTempPath(), "build-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.dll"), "fake dll content");
            File.WriteAllText(Path.Combine(dir, "test.exe"), "fake exe content");
            File.WriteAllText(Path.Combine(dir, "test.exe.config"), "<configuration/>");
            return dir;
        }
    }
}
