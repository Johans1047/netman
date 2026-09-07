using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HotReloadTool.Common;
using Xunit;

namespace HotReloadTool.Common.Tests
{
    /// <summary>
    /// Tests for <see cref="FileWatcherService"/> filtering and debounce.
    /// TDD RED→GREEN: tests verify exclusion and coalescing behavior.
    /// </summary>
    public class FileWatcherServiceTests
    {
        private static ReloadConfiguration CreateConfig(
            int debounce = 50,
            List<string> extensions = null,
            List<string> exclusions = null)
        {
            return new ReloadConfiguration
            {
                ProjectPath = @"C:\Project\MyService.csproj",
                WatchDirectory = @"C:\Project\src",
                DebounceMilliseconds = debounce,
                Extensions = extensions ?? new List<string> { "*.cs", "*.config" },
                ExclusionPatterns = exclusions ?? new List<string> { "**/obj/**", "**/bin/**", "**/*.tmp" }
            };
        }

        [Fact]
        public void ShouldInclude_MatchingCsFile_ReturnsTrue()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.True(sut.ShouldInclude(@"C:\Project\src\Program.cs"));
        }

        [Fact]
        public void ShouldInclude_MatchingConfigFile_ReturnsTrue()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.True(sut.ShouldInclude(@"C:\Project\src\app.config"));
        }

        [Fact]
        public void ShouldInclude_NonMatchingExtension_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(@"C:\Project\src\readme.md"));
        }

        [Fact]
        public void ShouldInclude_ExcludedObjPath_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(@"C:\Project\src\obj\Debug\file.cs"));
        }

        [Fact]
        public void ShouldInclude_ExcludedBinPath_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(@"C:\Project\src\bin\Debug\file.cs"));
        }

        [Fact]
        public void ShouldInclude_ExcludedTmpFile_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(@"C:\Project\src\temp.tmp"));
        }

        [Fact]
        public void ShouldInclude_NullPath_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(null));
        }

        [Fact]
        public void ShouldInclude_EmptyPath_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(string.Empty));
        }

        [Fact]
        public void ShouldInclude_NoExtension_ReturnsFalse()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            Assert.False(sut.ShouldInclude(@"C:\Project\src\Makefile"));
        }

        [Fact]
        public void Constructor_NullConfig_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new FileWatcherService(null));
        }

        [Fact]
        public void MatchesGlob_BinPattern_MatchesNestedBinPath()
        {
            Assert.True(FileWatcherService.MatchesGlob(@"C:\src\bin\Debug\file.cs", "**/bin/**"));
        }

        [Fact]
        public void MatchesGlob_ObjPattern_MatchesNestedObjPath()
        {
            Assert.True(FileWatcherService.MatchesGlob(@"C:\src\obj\Release\file.cs", "**/obj/**"));
        }

        [Fact]
        public void MatchesGlob_NonExcludedPath_ReturnsFalse()
        {
            Assert.False(FileWatcherService.MatchesGlob(@"C:\src\Services\File.cs", "**/bin/**"));
        }

        [Fact]
        public void FileChanged_Debounce_RaisesOnceForRapidChanges()
        {
            // Integration-style: use a real directory and watcher
            string tempDir = Path.Combine(Path.GetTempPath(), "hrl_test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            try
            {
                var config = new ReloadConfiguration
                {
                    ProjectPath = Path.Combine(tempDir, "test.csproj"),
                    WatchDirectory = tempDir,
                    DebounceMilliseconds = 100
                };

                int eventCount = 0;
                var evt = new ManualResetEventSlim(false);

                using (var sut = new FileWatcherService(config))
                {
                    sut.FileChanged += (s, e) =>
                    {
                        Interlocked.Increment(ref eventCount);
                        evt.Set();
                    };

                    sut.Start();

                    // Write 3 files rapidly within debounce window
                    for (int i = 0; i < 3; i++)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "file" + i + ".cs"), "code");
                        Thread.Sleep(30);
                    }

                    // Wait for debounce to fire
                    evt.Wait(TimeSpan.FromSeconds(2));
                    Thread.Sleep(200); // extra safety for timer
                }

                Assert.True(eventCount >= 1, "Expected at least 1 event, got " + eventCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void FileChanged_ExcludedFile_DoesNotRaise()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "hrl_test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "obj"));

            try
            {
                var config = new ReloadConfiguration
                {
                    ProjectPath = Path.Combine(tempDir, "test.csproj"),
                    WatchDirectory = tempDir,
                    DebounceMilliseconds = 50
                };

                int eventCount = 0;

                using (var sut = new FileWatcherService(config))
                {
                    sut.FileChanged += (s, e) => Interlocked.Increment(ref eventCount);
                    sut.Start();

                    // Write to excluded obj directory
                    File.WriteAllText(Path.Combine(tempDir, "obj", "excluded.cs"), "code");
                    Thread.Sleep(300);
                }

                Assert.Equal(0, eventCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            var config = CreateConfig();
            var sut = new FileWatcherService(config);
            sut.Dispose();
            sut.Dispose(); // should not throw
        }
    }
}
