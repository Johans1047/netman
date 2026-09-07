using HotReloadTool.Host;
using Xunit;

namespace HotReloadTool.Common.Tests
{
    /// <summary>
    /// Tests for <see cref="CommandLineArgs"/> parsing (CLI skeleton, Phase 1).
    /// </summary>
    public class CommandLineArgsTests
    {
        [Fact]
        public void Parse_NoArgs_ReturnsHelp()
        {
            var result = CommandLineArgs.Parse(new string[0]);
            Assert.Equal(HotReloadCommand.Help, result.Command);
        }

        [Fact]
        public void Parse_StartWithProject_ParsesCorrectly()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "--project", "MyService.csproj" });
            Assert.Equal(HotReloadCommand.Start, result.Command);
            Assert.Equal("MyService.csproj", result.ProjectPath);
        }

        [Fact]
        public void Parse_StartWithShortProject_ParsesCorrectly()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "-p", "MyService.csproj" });
            Assert.Equal(HotReloadCommand.Start, result.Command);
            Assert.Equal("MyService.csproj", result.ProjectPath);
        }

        [Fact]
        public void Parse_StartWithDebounceOverride_ParsesCorrectly()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "-p", "test.csproj", "--debounce", "300" });
            Assert.Equal(300, result.DebounceMilliseconds);
        }

        [Fact]
        public void Parse_StartWithDrainTimeout_ParsesCorrectly()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "-p", "test.csproj", "--drain-timeout", "5000" });
            Assert.Equal(5000, result.DrainTimeoutMilliseconds);
        }

        [Fact]
        public void Parse_StartWithPipeName_ParsesCorrectly()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "-p", "test.csproj", "--pipe-name", "my-pipe" });
            Assert.Equal("my-pipe", result.PipeName);
        }

        [Fact]
        public void Parse_StopCommand_ReturnsStop()
        {
            var result = CommandLineArgs.Parse(new[] { "stop" });
            Assert.Equal(HotReloadCommand.Stop, result.Command);
        }

        [Fact]
        public void Parse_StatusCommand_ReturnsStatus()
        {
            var result = CommandLineArgs.Parse(new[] { "status" });
            Assert.Equal(HotReloadCommand.Status, result.Command);
        }

        [Fact]
        public void Parse_HelpCommand_ReturnsHelp()
        {
            var result = CommandLineArgs.Parse(new[] { "help" });
            Assert.Equal(HotReloadCommand.Help, result.Command);
        }

        [Fact]
        public void Parse_UnknownCommand_ReturnsError()
        {
            var result = CommandLineArgs.Parse(new[] { "foobar" });
            Assert.Equal(HotReloadCommand.Help, result.Command);
            Assert.NotEmpty(result.Errors);
        }

        [Fact]
        public void Parse_StartWithoutProject_ReturnsError()
        {
            var result = CommandLineArgs.Parse(new[] { "start" });
            Assert.Contains(result.Errors, e => e.Contains("--project"));
        }

        [Fact]
        public void Parse_StartWithInvalidDebounce_ReturnsError()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "-p", "test.csproj", "--debounce", "abc" });
            Assert.Contains(result.Errors, e => e.Contains("--debounce"));
        }

        [Fact]
        public void Parse_PositionalProjectPath_Accepted()
        {
            var result = CommandLineArgs.Parse(new[] { "start", "MyService.csproj" });
            Assert.Equal("MyService.csproj", result.ProjectPath);
        }
    }
}
