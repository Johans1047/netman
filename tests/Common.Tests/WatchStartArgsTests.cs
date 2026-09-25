using HotReloadTool.Host;
using Xunit;

namespace HotReloadTool.Common.Tests
{
    /// <summary>
    /// Tests for <see cref="CommandLineArgs.BuildWatchStartArgs"/> — the seam
    /// that builds the child <c>start</c> argument list for watch mode
    /// (Phase 4 watch-forwarding fix).
    /// </summary>
    public class WatchStartArgsTests
    {
        private static int IndexOfFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == flag)
                    return i;
            }
            return -1;
        }

        private static void AssertFlagValue(string[] args, string flag, string expectedValue)
        {
            int index = IndexOfFlag(args, flag);
            Assert.True(index >= 0, "Expected flag '" + flag + "' in: " + string.Join(" ", args));
            Assert.True(index + 1 < args.Length, "Flag '" + flag + "' has no value.");
            Assert.Equal(expectedValue, args[index + 1]);
        }

        [Fact]
        public void BuildWatchStartArgs_DefaultProcessMode_OmitsBuildRecycleFlags()
        {
            var parsed = CommandLineArgs.Parse(new[] { "start", "-p", "MyService.csproj", "--watch" });
            string[] childArgs = parsed.BuildWatchStartArgs();

            Assert.Equal("start", childArgs[0]);
            AssertFlagValue(childArgs, "-p", "MyService.csproj");

            // Default process mode: the four build-recycle flags must be absent.
            Assert.True(IndexOfFlag(childArgs, "--mode") < 0, "--mode must not be forwarded in default process mode.");
            Assert.True(IndexOfFlag(childArgs, "--extensions") < 0, "--extensions must not be forwarded when not set.");
            Assert.True(IndexOfFlag(childArgs, "--deploy-to") < 0, "--deploy-to must not be forwarded when not set.");
            Assert.True(IndexOfFlag(childArgs, "--recycle-target") < 0, "--recycle-target must not be forwarded when not set.");
        }

        [Fact]
        public void BuildWatchStartArgs_BuildRecycleMode_ForwardsAllFourFlags()
        {
            var parsed = CommandLineArgs.Parse(new[]
            {
                "start", "-p", "Lib.csproj", "--watch",
                "--mode", "build-recycle",
                "--extensions", "*.vb,*.config",
                "--deploy-to", @"C:\site\Bin",
                "--recycle-target", @"C:\site\web.config"
            });
            string[] childArgs = parsed.BuildWatchStartArgs();

            Assert.Equal("start", childArgs[0]);
            AssertFlagValue(childArgs, "-p", "Lib.csproj");
            AssertFlagValue(childArgs, "--mode", "build-recycle");
            AssertFlagValue(childArgs, "--extensions", "*.vb,*.config");
            AssertFlagValue(childArgs, "--deploy-to", @"C:\site\Bin");
            AssertFlagValue(childArgs, "--recycle-target", @"C:\site\web.config");
        }

        [Fact]
        public void BuildWatchStartArgs_ForwardsExistingTimingAndPipeFlags()
        {
            var parsed = CommandLineArgs.Parse(new[]
            {
                "start", "-p", "MyService.csproj", "--watch",
                "--debounce", "750",
                "--drain-timeout", "4000",
                "--pipe-name", "custom-pipe"
            });
            string[] childArgs = parsed.BuildWatchStartArgs();

            AssertFlagValue(childArgs, "--debounce", "750");
            AssertFlagValue(childArgs, "--drain-timeout", "4000");
            AssertFlagValue(childArgs, "--pipe-name", "custom-pipe");
            Assert.True(IndexOfFlag(childArgs, "--mode") < 0, "--mode must stay absent for process mode.");
        }

        [Fact]
        public void BuildWatchStartArgs_ExplicitProcessMode_OmitsModeAndBuildRecycleFlags()
        {
            var parsed = CommandLineArgs.Parse(new[]
            {
                "start", "-p", "MyService.csproj", "--watch", "--mode", "process"
            });
            string[] childArgs = parsed.BuildWatchStartArgs();

            // Explicit default mode behaves like the implicit default:
            // unchanged child args, no build-recycle flags.
            Assert.True(IndexOfFlag(childArgs, "--mode") < 0, "--mode process is the default and must not be forwarded.");
            Assert.True(IndexOfFlag(childArgs, "--deploy-to") < 0, "--deploy-to must not be forwarded in process mode.");
            Assert.True(IndexOfFlag(childArgs, "--recycle-target") < 0, "--recycle-target must not be forwarded in process mode.");
        }
    }
}
