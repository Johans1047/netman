using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HotReloadTool.Common;
using HotReloadTool.Contracts;
using HotReloadTool.Host;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Integration tests for <see cref="ReloadOrchestrator"/> that exercise the full
    /// reload cycle with real Named Pipes, real processes, and real file I/O.
    /// </summary>
    /// <remarks>
    /// These tests use the <see cref="FixtureWorker"/> console app as both old and
    /// new worker. The old worker is launched directly by the test; the new worker
    /// is launched by a custom <see cref="IProcessManager"/> that invokes the
    /// fixture worker in "receive-state" mode.
    /// </remarks>
    public class ReloadOrchestratorIntegrationTests
    {
        // ─── Warmup ──────────────────────────────────────────────────────────

        /// <summary>
        /// Static constructor performs a one-time MSBuild warmup so that the
        /// first integration test does not pay the cost of loading the MSBuild
        /// engine (which can take 30+ seconds on first use).
        /// </summary>
        static ReloadOrchestratorIntegrationTests()
        {
            string exePath = GetFixtureExePath();
            if (exePath != null)
            {
                string projectPath = GetFixtureProjectPath();
                if (File.Exists(projectPath))
                {
                    try
                    {
                        var warmupBuild = new BuildOrchestrator();
                        warmupBuild.Build(projectPath);
                    }
                    catch
                    {
                        // Warmup failure is non-fatal; tests will still attempt the build.
                    }
                }
            }
        }

        // ─── Integration Process Manager ─────────────────────────────────────

        /// <summary>
        /// Process manager for integration tests that tracks a real old worker
        /// process and launches the fixture worker in "receive-state" mode for
        /// new worker launches.
        /// </summary>
        private class IntegrationProcessManager : IProcessManager
        {
            private Process _currentProcess;
            private readonly string _pipeName;
            private readonly string _stateFilePath;
            public List<string> LaunchedPaths { get; private set; }
            public bool LastTerminateWasGraceful { get; private set; }

            public IntegrationProcessManager(string pipeName, string stateFilePath, Process oldWorkerProcess)
            {
                _pipeName = pipeName;
                _stateFilePath = stateFilePath;
                _currentProcess = oldWorkerProcess;
                LaunchedPaths = new List<string>();
            }

            public Process Current { get { return _currentProcess; } }

            public bool IsRunning
            {
                get { return _currentProcess != null && !_currentProcess.HasExited; }
            }

            public void Launch(string executablePath, string arguments = null)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    UseShellExecute = false,
                    Arguments = _pipeName + " --receive-state " + _stateFilePath
                };

                _currentProcess = new Process { StartInfo = startInfo };
                _currentProcess.EnableRaisingEvents = true;
                _currentProcess.Exited += OnProcessExited;
                _currentProcess.Start();

                LaunchedPaths.Add(executablePath);
            }

            public bool TerminateGracefully(int timeoutMilliseconds)
            {
                if (_currentProcess == null || _currentProcess.HasExited)
                    return true;

                bool graceful = false;
                try
                {
                    _currentProcess.CloseMainWindow();
                    graceful = _currentProcess.WaitForExit(timeoutMilliseconds);
                }
                catch
                {
                    // Process already exited
                }

                if (graceful)
                {
                    LastTerminateWasGraceful = true;
                    return true;
                }

                KillWorker();
                return false;
            }

            public void KillWorker()
            {
                if (_currentProcess == null || _currentProcess.HasExited)
                    return;

                try
                {
                    _currentProcess.Kill();
                    _currentProcess.WaitForExit(5000);
                }
                catch
                {
                    // Already exited
                }

                LastTerminateWasGraceful = false;
            }

            public event EventHandler Exited;

            private void OnProcessExited(object sender, EventArgs e)
            {
                var handler = Exited;
                if (handler != null)
                    handler(this, e);
            }

            public void Dispose()
            {
                if (_currentProcess != null)
                {
                    _currentProcess.Exited -= OnProcessExited;
                    try
                    {
                        if (!_currentProcess.HasExited)
                        {
                            _currentProcess.Kill();
                            _currentProcess.WaitForExit(5000);
                        }
                    }
                    catch { }
                    _currentProcess.Dispose();
                    _currentProcess = null;
                }
            }
        }

        // ─── Fake File Watcher ───────────────────────────────────────────────

        /// <summary>
        /// Minimal file watcher that allows the test to trigger a file change event.
        /// </summary>
        private class FakeFileWatcher : IFileWatcherService
        {
            public void Start() { }
            public void Stop() { }

            public void RaiseFileChanged()
            {
                var handler = FileChanged;
                if (handler != null)
                    handler(this, new FileChangedEventArgs(new List<string> { "/fake/path.cs" }));
            }

            public event EventHandler<FileChangedEventArgs> FileChanged;
            public event EventHandler<ErrorEventArgs> Error;
            public void Dispose() { }
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the path to the FixtureWorker.exe, or null if not built.
        /// </summary>
        private static string GetFixtureExePath()
        {
            string testDir = Path.GetDirectoryName(typeof(ReloadOrchestratorIntegrationTests).Assembly.Location);
            // testDir is tests\Host.Tests\bin\Debug\ -> go up 2 to tests\Host.Tests\
            string candidate = Path.GetFullPath(Path.Combine(testDir, "..", "..", "Fixtures", "bin", "Debug", "FixtureWorker.exe"));
            if (File.Exists(candidate))
                return candidate;

            return null;
        }

        /// <summary>
        /// Copies the FixtureWorker and its dependencies to a temp directory and returns
        /// the path to the copied exe. This prevents the old worker process from locking
        /// the build output directory (which would block MSBuild from rebuilding).
        /// </summary>
        private static string CopyFixtureToTemp(string originalExePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "hotreload-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string sourceDir = Path.GetDirectoryName(originalExePath);

            // Copy all files from the build output directory
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(tempDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            return Path.Combine(tempDir, Path.GetFileName(originalExePath));
        }

        /// <summary>
        /// Gets the path to the FixtureWorker.csproj.
        /// </summary>
        private static string GetFixtureProjectPath()
        {
            string testDir = Path.GetDirectoryName(typeof(ReloadOrchestratorIntegrationTests).Assembly.Location);
            // testDir is tests\Host.Tests\bin\Debug\ -> go up 2 to tests\Host.Tests\
            string candidate = Path.GetFullPath(Path.Combine(testDir, "..", "..", "Fixtures", "FixtureWorker.csproj"));
            if (File.Exists(candidate))
                return candidate;

            return null;
        }

        /// <summary>
        /// Launches the fixture worker as a separate process.
        /// </summary>
        private static Process LaunchFixtureWorker(string exePath, string pipeName, string mode)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = false,
                Arguments = pipeName + " " + mode
            };

            return Process.Start(startInfo);
        }

        /// <summary>
        /// Forcefully kills a process if it's still running.
        /// </summary>
        private static void KillProcess(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch { }
        }

        /// <summary>
        /// Polls until the predicate is true or the timeout elapses.
        /// </summary>
        private static bool WaitFor(Func<bool> predicate, int timeoutMilliseconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (predicate())
                    return true;
                Thread.Sleep(50);
            }
            return predicate();
        }

        /// <summary>
        /// Creates a configuration for integration tests pointing to the FixtureWorker project.
        /// </summary>
        private static ReloadConfiguration CreateIntegrationConfig(string pipeName, string projectPath, int drainTimeout = 3000, int stateTransferTimeout = 5000)
        {
            return new ReloadConfiguration
            {
                ProjectPath = projectPath,
                PipeName = pipeName,
                DrainTimeoutMilliseconds = drainTimeout,
                StateTransferTimeoutMilliseconds = stateTransferTimeout,
                DebounceMilliseconds = 50
            };
        }

        // ─── Test 5.1: Full reload cycle — state survives ─────────────────────

        /// <summary>
        /// 5.1: Verifies that when a stateful fixture worker (implementing IStatefulLifecycle
        /// with state {Counter: 42}) goes through a full reload cycle, the new worker
        /// receives the same state via Named Pipes.
        /// </summary>
        [Fact]
        public void FullReloadCycle_StateSurvives_NewWorkerReceivesState()
        {
            string exePath = GetFixtureExePath();
            if (exePath == null)
            {
                // FixtureWorker not built — skip with a clear message
                return;
            }

            string projectPath = GetFixtureProjectPath();
            string pipeName = "test-5-1-" + Guid.NewGuid().ToString("N");
            string stateFilePath = Path.Combine(Path.GetTempPath(), "hotreload-5-1-" + Guid.NewGuid().ToString("N") + ".json");
            string tempExePath = CopyFixtureToTemp(exePath);
            Process oldWorker = null;

            try
            {
                // Step 1: Launch the old fixture worker in "normal" mode (has state {Counter: 42}).
                // Use the temp copy so the original build output is not file-locked.
                oldWorker = LaunchFixtureWorker(tempExePath, pipeName, "normal");
                Assert.NotNull(oldWorker);
                // Give the worker time to start listening on the pipe
                Thread.Sleep(1000);

                // Step 2: Create config and orchestrator with real services
                var config = CreateIntegrationConfig(pipeName, projectPath);
                var watcher = new FakeFileWatcher();
                var build = new BuildOrchestrator();
                var shadow = new ShadowCopyManager(Guid.NewGuid().ToString("N"));
                var drain = new DrainCoordinator(pipeName);
                var stateTransfer = new StateTransferHost(pipeName, 5000);
                var processManager = new IntegrationProcessManager(pipeName, stateFilePath, oldWorker);

                using (var orchestrator = new ReloadOrchestrator(config, watcher, build, shadow, drain, stateTransfer, processManager))
                {
                    orchestrator.Start();

                    // Step 3: Trigger a reload
                    watcher.RaiseFileChanged();

                    // Step 4: Wait for the new worker to write its received state to the file.
                    // MSBuild compilation can be slow on first run, so allow generous time.
                    bool stateWritten = WaitFor(() =>
                    {
                        try
                        {
                            return File.Exists(stateFilePath) && new FileInfo(stateFilePath).Length > 0;
                        }
                        catch
                        {
                            return false;
                        }
                    }, 90000);

                    Assert.True(stateWritten, "New worker should write state file within 90 seconds.");

                    // Step 5: Verify the state was transferred intact
                    string receivedState = File.ReadAllText(stateFilePath).Trim();
                    Assert.NotEqual("NULL", receivedState);
                    Assert.Contains("Counter", receivedState);
                    Assert.Contains("42", receivedState);

                    // Step 6: Verify orchestrator completed the cycle
                    Assert.True(WaitFor(() => orchestrator.State == ReloadState.Idle, 5000),
                        "Orchestrator should return to Idle after reload cycle.");
                }
            }
            finally
            {
                KillProcess(oldWorker);
                try { oldWorker.Dispose(); } catch { }
                try { if (File.Exists(stateFilePath)) File.Delete(stateFilePath); } catch { }
            }
        }

        // ─── Test 5.2: Stateless restart — no crash ──────────────────────────

        /// <summary>
        /// 5.2: Verifies that a worker that does NOT push state (stateless mode)
        /// results in the new worker starting with null/default state, with no crash.
        /// </summary>
        [Fact]
        public void StatelessRestart_NewWorkerReceivesNullState()
        {
            string exePath = GetFixtureExePath();
            if (exePath == null)
            {
                return;
            }

            string projectPath = GetFixtureProjectPath();
            string pipeName = "test-5-2-" + Guid.NewGuid().ToString("N");
            string stateFilePath = Path.Combine(Path.GetTempPath(), "hotreload-5-2-" + Guid.NewGuid().ToString("N") + ".json");
            string tempExePath = CopyFixtureToTemp(exePath);
            Process oldWorker = null;

            try
            {
                // Step 1: Launch the old fixture worker in "stateless" mode (responds to drain but does NOT push state).
                // Use the temp copy so the original build output is not file-locked.
                oldWorker = LaunchFixtureWorker(tempExePath, pipeName, "stateless");
                Assert.NotNull(oldWorker);
                Thread.Sleep(1000);

                // Step 2: Create config and orchestrator with real services
                var config = CreateIntegrationConfig(pipeName, projectPath);
                var watcher = new FakeFileWatcher();
                var build = new BuildOrchestrator();
                var shadow = new ShadowCopyManager(Guid.NewGuid().ToString("N"));
                var drain = new DrainCoordinator(pipeName);
                var stateTransfer = new StateTransferHost(pipeName, 2000);
                var processManager = new IntegrationProcessManager(pipeName, stateFilePath, oldWorker);

                using (var orchestrator = new ReloadOrchestrator(config, watcher, build, shadow, drain, stateTransfer, processManager))
                {
                    orchestrator.Start();

                    // Step 3: Trigger a reload
                    watcher.RaiseFileChanged();

                    // Step 4: Wait for the new worker to write its received state.
                    // MSBuild compilation can be slow on first run, so allow generous time.
                    bool stateWritten = WaitFor(() =>
                    {
                        try
                        {
                            return File.Exists(stateFilePath) && new FileInfo(stateFilePath).Length > 0;
                        }
                        catch
                        {
                            return false;
                        }
                    }, 90000);

                    Assert.True(stateWritten, "New worker should write state file within 90 seconds.");

                    // Step 5: Verify the new worker received NULL state (stateless restart)
                    string receivedState = File.ReadAllText(stateFilePath).Trim();
                    Assert.Equal("NULL", receivedState);

                    // Step 6: Verify orchestrator completed the cycle (no crash, no error state)
                    Assert.True(WaitFor(() => orchestrator.State == ReloadState.Idle, 5000),
                        "Orchestrator should return to Idle after stateless reload.");
                }
            }
            finally
            {
                KillProcess(oldWorker);
                try { oldWorker.Dispose(); } catch { }
                try { if (File.Exists(stateFilePath)) File.Delete(stateFilePath); } catch { }
            }
        }

        // ─── Test 5.3: Drain timeout forces termination ───────────────────────

        /// <summary>
        /// 5.3: Verifies that when a worker ignores the drain request (responds too slowly),
        /// the orchestrator force-terminates the worker after the drain timeout elapses
        /// and proceeds with a stateless restart.
        /// </summary>
        [Fact]
        public void DrainTimeout_WorkerKilled_OrchestratorProceeds()
        {
            string exePath = GetFixtureExePath();
            if (exePath == null)
            {
                return;
            }

            string projectPath = GetFixtureProjectPath();
            string pipeName = "test-5-3-" + Guid.NewGuid().ToString("N");
            string stateFilePath = Path.Combine(Path.GetTempPath(), "hotreload-5-3-" + Guid.NewGuid().ToString("N") + ".json");
            string tempExePath = CopyFixtureToTemp(exePath);
            Process oldWorker = null;

            try
            {
                // Step 1: Launch the old fixture worker in "slowdrain" mode (takes 10s to respond).
                // Use the temp copy so the original build output is not file-locked.
                oldWorker = LaunchFixtureWorker(tempExePath, pipeName, "slowdrain");
                Assert.NotNull(oldWorker);
                Thread.Sleep(1000);

                // Step 2: Create config with a SHORT drain timeout (1s) so the worker can't respond in time
                var config = CreateIntegrationConfig(pipeName, projectPath, drainTimeout: 1000, stateTransferTimeout: 2000);
                var watcher = new FakeFileWatcher();
                var build = new BuildOrchestrator();
                var shadow = new ShadowCopyManager(Guid.NewGuid().ToString("N"));
                var drain = new DrainCoordinator(pipeName);
                var stateTransfer = new StateTransferHost(pipeName, 2000);
                var processManager = new IntegrationProcessManager(pipeName, stateFilePath, oldWorker);

                using (var orchestrator = new ReloadOrchestrator(config, watcher, build, shadow, drain, stateTransfer, processManager))
                {
                    orchestrator.Start();

                    // Step 3: Trigger a reload
                    watcher.RaiseFileChanged();

                    // Step 4: Wait for the old worker to be terminated (killed)
                    bool oldWorkerKilled = WaitFor(() => oldWorker.HasExited, 60000);

                    Assert.True(oldWorkerKilled, "Old worker should be killed within 60 seconds after drain timeout.");

                    // Step 5: Verify the orchestrator proceeded to launch a new worker
                    Assert.True(WaitFor(() => processManager.LaunchedPaths.Count > 0, 10000),
                        "Orchestrator should launch a new worker after killing the old one.");

                    // Step 6: Verify orchestrator completed the cycle
                    Assert.True(WaitFor(() => orchestrator.State == ReloadState.Idle, 5000),
                        "Orchestrator should return to Idle after drain-timeout reload.");

                    // Step 7: Verify the termination was NOT graceful (Kill fallback was used)
                    Assert.False(processManager.LastTerminateWasGraceful,
                        "Worker should have been force-killed, not gracefully terminated.");
                }
            }
            finally
            {
                KillProcess(oldWorker);
                try { oldWorker.Dispose(); } catch { }
                try { if (File.Exists(stateFilePath)) File.Delete(stateFilePath); } catch { }
            }
        }
    }
}
