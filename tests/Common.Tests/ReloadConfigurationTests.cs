using System;
using HotReloadTool.Common;
using Xunit;

namespace HotReloadTool.Common.Tests
{
    /// <summary>
    /// Tests for <see cref="ReloadConfiguration"/> validation and defaults.
    /// TDD RED→GREEN: tests written to validate config rules.
    /// </summary>
    public class ReloadConfigurationTests
    {
        [Fact]
        public void Validate_MissingProjectPath_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration();
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_WhiteSpaceProjectPath_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration { ProjectPath = "   " };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_ValidConfig_DoesNotThrow()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = @"C:\Service\MyService.csproj"
            };
            config.Validate();
        }

        [Fact]
        public void Validate_NegativeDebounce_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                DebounceMilliseconds = -1
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_NegativeDrainTimeout_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                DrainTimeoutMilliseconds = -100
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_NegativeStateTransferTimeout_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                StateTransferTimeoutMilliseconds = -1
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_EmptyExtensions_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                Extensions = new System.Collections.Generic.List<string>()
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_NullExtensions_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                Extensions = null
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Validate_ExtensionWithEmptyEntry_ThrowsArgumentException()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = "test.csproj",
                Extensions = new System.Collections.Generic.List<string> { "*.cs", "" }
            };
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Fact]
        public void Defaults_AreSensible()
        {
            var config = new ReloadConfiguration();

            Assert.Equal(new[] { "*.cs", "*.config" }, config.Extensions);
            Assert.Contains("**/obj/**", config.ExclusionPatterns);
            Assert.Contains("**/bin/**", config.ExclusionPatterns);
            Assert.Equal(500, config.DebounceMilliseconds);
            Assert.Equal(3000, config.DrainTimeoutMilliseconds);
            Assert.Equal(5000, config.StateTransferTimeoutMilliseconds);
            Assert.Equal("hotreload-state", config.PipeName);
            Assert.Equal(3, config.RetainedShadowCopies);
        }

        [Fact]
        public void GetEffectiveWatchDirectory_ExplicitValue_TakesPrecedence()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = @"C:\Project\MyService.csproj",
                WatchDirectory = @"D:\Other\Src"
            };
            Assert.Equal(@"D:\Other\Src", config.GetEffectiveWatchDirectory());
        }

        [Fact]
        public void GetEffectiveWatchDirectory_NoExplicit_ReturnsProjectParent()
        {
            var config = new ReloadConfiguration
            {
                ProjectPath = @"C:\Project\MyService.csproj"
            };
            Assert.Equal(@"C:\Project", config.GetEffectiveWatchDirectory());
        }

        [Fact]
        public void GetEffectiveWatchDirectory_NeitherSet_ReturnsNull()
        {
            var config = new ReloadConfiguration();
            Assert.Null(config.GetEffectiveWatchDirectory());
        }
    }
}
