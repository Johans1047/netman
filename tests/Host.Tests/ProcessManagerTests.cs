using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="ProcessManager"/> — launch, graceful termination,
    /// CTRL_CLOSE_EVENT + Kill fallback. Covers threat-matrix cases.
    /// TDD RED→GREEN: tests verify lifecycle and kill fallback.
    /// </summary>
    public class ProcessManagerTests : IDisposable
    {
        private readonly ProcessManager _manager = new ProcessManager();

        [Fact]
        public void Launch_ValidExecutable_StartsProcess()
        {
            // Use a long-running system process as fixture
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");

            _manager.Launch(pingExe, "127.0.0.1 -n 60");

            Assert.True(_manager.IsRunning, "Process should be running after launch.");
            Assert.NotNull(_manager.Current);
        }

        [Fact]
        public void Launch_NonExistentExecutable_ThrowsFileNotFoundException()
        {
            Assert.Throws<FileNotFoundException>(
                () => _manager.Launch(@"C:\nonexistent\worker.exe"));
        }

        [Fact]
        public void Launch_AlreadyRunning_ThrowsInvalidOperationException()
        {
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");

            _manager.Launch(pingExe, "127.0.0.1 -n 60");

            Assert.Throws<InvalidOperationException>(
                () => _manager.Launch(pingExe, "127.0.0.1 -n 60"));
        }

        [Fact]
        public void TerminateGracefully_TimeoutZero_KillsImmediately()
        {
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");

            _manager.Launch(pingExe, "127.0.0.1 -n 60");
            bool graceful = _manager.TerminateGracefully(0);

            Assert.False(graceful, "With timeout=0, should report non-graceful (Kill).");
            Assert.False(_manager.IsRunning, "Process should be terminated.");
        }

        [Fact]
        public void TerminateGracefully_ProcessExits_ReturnsTrue()
        {
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");

            // ping with -n 2 finishes in ~1 second
            _manager.Launch(pingExe, "127.0.0.1 -n 2");
            bool graceful = _manager.TerminateGracefully(5000);

            Assert.True(graceful, "Process should exit gracefully within timeout.");
        }

        [Fact]
        public void TerminateGracefully_IgnoresCtrlClose_KillsAfterTimeout()
        {
            // Use cmd.exe with /C running a long command — cmd handles CTRL_CLOSE
            // but the child ping keeps the process tree alive
            string cmdExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            _manager.Launch(cmdExe, "/c ping 127.0.0.1 -n 60");
            bool graceful = _manager.TerminateGracefully(1000);

            // After timeout, process should be killed regardless
            Assert.False(_manager.IsRunning, "Process should be dead after kill fallback.");
        }

        [Fact]
        public void KillWorker_ForceTerminatesRunningProcess()
        {
            string pingExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");

            _manager.Launch(pingExe, "127.0.0.1 -n 60");
            _manager.KillWorker();

            Assert.False(_manager.IsRunning, "Process should be dead after KillWorker.");
        }

        [Fact]
        public void IsRunning_NoProcess_ReturnsFalse()
        {
            Assert.False(_manager.IsRunning);
        }

        public void Dispose()
        {
            if (_manager != null)
                _manager.Dispose();
        }
    }
}
