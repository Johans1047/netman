using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HotReloadTool.Common;
using HotReloadTool.Contracts;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="ReloadOrchestrator"/> — state machine orchestration.
    /// TDD RED→GREEN: tests verify build-failure preservation, queue-one semantics,
    /// and unexpected worker exit recovery.
    /// </summary>
    public class ReloadOrchestratorTests
    {
        // ─── Fakes ───────────────────────────────────────────────────────────

        /// <summary>
        /// Configurable build orchestrator fake. Can be set to succeed or fail,
        /// and tracks invocation count for queue-one verification.
        /// </summary>
        public class FakeBuildOrchestrator : IBuildOrchestrator
        {
            public bool ShouldSucceed { get; set; }
            public int DelayMilliseconds { get; set; }

            private int _buildCount;
            public int BuildCount { get { return _buildCount; } }

            public BuildResult Build(string projectPath)
            {
                Interlocked.Increment(ref _buildCount);
                if (DelayMilliseconds > 0)
                    Thread.Sleep(DelayMilliseconds);

                if (ShouldSucceed)
                    return new BuildResult { Success = true, Output = new List<string>() };
                return BuildResult.Failed("simulated build failure");
            }
        }

        /// <summary>
        /// In-memory shadow-copy manager fake. Tracks copies and returns
        /// configurable latest shadow path.
        /// </summary>
        public class FakeShadowCopyManager : IShadowCopyManager
        {
            public string ShadowPath { get; set; }
            public string LatestShadowCopy { get; set; }

            private int _copyCount;
            private int _cleanupCount;
            public int CopyCount { get { return _copyCount; } }
            public int CleanupCount { get { return _cleanupCount; } }

            public string CopyToTemp(string buildOutputPath)
            {
                Interlocked.Increment(ref _copyCount);
                return ShadowPath;
            }

            public void CleanupOldCopies()
            {
                Interlocked.Increment(ref _cleanupCount);
            }

            public string GetLatestShadowCopy()
            {
                return LatestShadowCopy;
            }
        }

        /// <summary>
        /// Configurable drain coordinator fake.
        /// </summary>
        public class FakeDrainCoordinator : IDrainCoordinator
        {
            public bool DrainResult { get; set; }

            private int _drainCount;
            public int DrainCount { get { return _drainCount; } }

            public bool Drain(int timeoutMilliseconds)
            {
                Interlocked.Increment(ref _drainCount);
                return DrainResult;
            }
        }

        /// <summary>
        /// In-memory state transfer host fake.
        /// </summary>
        public class FakeStateTransferHost : IStateTransferHost
        {
            public string StateToReturn { get; set; }

            private int _receiveCount;
            private int _deliverCount;
            public int ReceiveCount { get { return _receiveCount; } }
            public int DeliverCount { get { return _deliverCount; } }

            public string HeldState { get; private set; }

            public string BeginReceive(int timeoutMilliseconds = 0)
            {
                Interlocked.Increment(ref _receiveCount);
                HeldState = StateToReturn;
                return StateToReturn;
            }

            public bool DeliverState(int timeoutMilliseconds = 0)
            {
                Interlocked.Increment(ref _deliverCount);
                return true;
            }

            public void Dispose() { }
        }

        /// <summary>
        /// Process manager fake that tracks Launch/Terminate calls and can
        /// raise the Exited event on demand.
        /// </summary>
        public class FakeProcessManager : IProcessManager
        {
            public List<string> LaunchedPaths { get; private set; }
            public bool IsRunningValue { get; set; }

            private int _terminateCount;
            private int _killCount;
            public int TerminateCount { get { return _terminateCount; } }
            public int KillCount { get { return _killCount; } }

            public FakeProcessManager()
            {
                LaunchedPaths = new List<string>();
            }

            public Process Current { get { return null; } }

            public bool IsRunning { get { return IsRunningValue; } }

            public void Launch(string executablePath, string arguments = null)
            {
                LaunchedPaths.Add(executablePath);
                IsRunningValue = true;
            }

            public bool TerminateGracefully(int timeoutMilliseconds)
            {
                Interlocked.Increment(ref _terminateCount);
                IsRunningValue = false;
                return true;
            }

            public void KillWorker()
            {
                Interlocked.Increment(ref _killCount);
                IsRunningValue = false;
            }

            public void RaiseExited()
            {
                var handler = Exited;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }

            public event EventHandler Exited;

            public void Dispose() { }
        }

        /// <summary>
        /// File watcher fake that allows manual triggering of FileChanged events.
        /// </summary>
        public class FakeFileWatcherService : IFileWatcherService
        {
            private int _startCount;
            private int _stopCount;
            public int StartCount { get { return _startCount; } }
            public int StopCount { get { return _stopCount; } }

            public void Start()
            {
                Interlocked.Increment(ref _startCount);
            }

            public void Stop()
            {
                Interlocked.Increment(ref _stopCount);
            }

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

        private static ReloadConfiguration CreateConfig()
        {
            return new ReloadConfiguration
            {
                ProjectPath = @"C:\fake\project.csproj",
                DebounceMilliseconds = 50,
                DrainTimeoutMilliseconds = 100,
                StateTransferTimeoutMilliseconds = 100,
                PipeName = "test-pipe"
            };
        }

        private static ReloadOrchestrator CreateOrchestrator(
            ReloadConfiguration config,
            FakeFileWatcherService watcher,
            FakeBuildOrchestrator build = null,
            FakeShadowCopyManager shadow = null,
            FakeDrainCoordinator drain = null,
            FakeStateTransferHost state = null,
            FakeProcessManager process = null)
        {
            return new ReloadOrchestrator(
                config,
                watcher,
                build ?? new FakeBuildOrchestrator(),
                shadow ?? new FakeShadowCopyManager(),
                drain ?? new FakeDrainCoordinator(),
                state ?? new FakeStateTransferHost(),
                process ?? new FakeProcessManager());
        }

        /// <summary>
        /// Polls until the predicate is true or the timeout elapses.
        /// Returns true if the condition was met.
        /// </summary>
        private static bool WaitFor(Func<bool> predicate, int timeoutMilliseconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (predicate())
                    return true;
                Thread.Sleep(20);
            }
            return predicate();
        }

        // ─── Tests ───────────────────────────────────────────────────────────

        // 4.2: Build failure keeps old worker (no termination called)
        [Fact]
        public void ReloadCycle_BuildFails_OldWorkerPreserved()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var build = new FakeBuildOrchestrator { ShouldSucceed = false };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, process: process))
            {
                orchestrator.Start();
                watcher.RaiseFileChanged();

                // Wait for the reload cycle to complete
                Thread.Sleep(500);

                Assert.Equal(1, build.BuildCount);
                Assert.Equal(0, process.TerminateCount);
                Assert.Equal(0, process.KillCount);
                Assert.Equal(ReloadState.Failed, orchestrator.State);
            }
        }

        // 4.2 GREEN: Success path proceeds through all phases
        [Fact]
        public void ReloadCycle_BuildSucceeds_ProceedsToLaunch()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var build = new FakeBuildOrchestrator { ShouldSucceed = true };
            var shadow = new FakeShadowCopyManager { ShadowPath = @"C:\shadow\copy1" };
            var drain = new FakeDrainCoordinator { DrainResult = true };
            var state = new FakeStateTransferHost { StateToReturn = "{\"key\":\"value\"}" };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, shadow, drain, state, process))
            {
                orchestrator.Start();
                watcher.RaiseFileChanged();

                // Wait for the cycle to complete (cleanup is the last step before Idle)
                Assert.True(WaitFor(() => shadow.CleanupCount == 1, 5000));

                Assert.Equal(1, build.BuildCount);
                Assert.Equal(1, shadow.CopyCount);
                Assert.Equal(1, drain.DrainCount);
                Assert.Equal(1, state.ReceiveCount);
                Assert.Equal(1, process.TerminateCount);
                Assert.Equal(1, process.LaunchedPaths.Count);
                Assert.Equal(@"C:\shadow\copy1\project.exe", process.LaunchedPaths[0]);
                Assert.Equal(1, state.DeliverCount);
                Assert.Equal(1, shadow.CleanupCount);
                Assert.Equal(ReloadState.Idle, orchestrator.State);
            }
        }

        // 4.3: File change during build queues one reload (no concurrent builds)
        [Fact]
        public void ReloadCycle_FileChangeDuringBuild_QueuesOneReload()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            // Build takes 500ms — enough time to trigger a second change
            var build = new FakeBuildOrchestrator { ShouldSucceed = true, DelayMilliseconds = 500 };
            var shadow = new FakeShadowCopyManager { ShadowPath = @"C:\shadow\copy1" };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, shadow, process: process))
            {
                orchestrator.Start();
                watcher.RaiseFileChanged();

                // Wait for the first build to actually start, then trigger a second change
                Assert.True(WaitFor(() => build.BuildCount >= 1 && orchestrator.IsReloading, 3000));
                watcher.RaiseFileChanged();

                // Wait for both cycles to complete (2 launches = both cycles done)
                Assert.True(WaitFor(() => process.LaunchedPaths.Count == 2, 5000));

                // Exactly 2 builds: the first one + the queued one
                Assert.Equal(2, build.BuildCount);
                Assert.Equal(2, shadow.CopyCount);
                Assert.Equal(2, process.LaunchedPaths.Count);
                Assert.Equal(ReloadState.Idle, orchestrator.State);
            }
        }

        // 4.3: Multiple rapid file changes during build still queue only one
        [Fact]
        public void ReloadCycle_MultipleFileChangesDuringBuild_QueuesOnlyOne()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var build = new FakeBuildOrchestrator { ShouldSucceed = true, DelayMilliseconds = 500 };
            var shadow = new FakeShadowCopyManager { ShadowPath = @"C:\shadow\copy1" };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, shadow, process: process))
            {
                orchestrator.Start();
                watcher.RaiseFileChanged();

                // Wait for the first build to start, then rapid-fire 5 changes
                Assert.True(WaitFor(() => build.BuildCount >= 1 && orchestrator.IsReloading, 3000));
                watcher.RaiseFileChanged();
                watcher.RaiseFileChanged();
                watcher.RaiseFileChanged();
                watcher.RaiseFileChanged();
                watcher.RaiseFileChanged();

                // Wait for both cycles to complete (2 launches = both cycles done)
                Assert.True(WaitFor(() => process.LaunchedPaths.Count == 2, 5000));

                // Only 2 builds: first + one queued (not 6)
                Assert.Equal(2, build.BuildCount);
                Assert.Equal(ReloadState.Idle, orchestrator.State);
            }
        }

        // 4.4: Worker exits unexpectedly triggers restart from last shadow copy
        [Fact]
        public void ReloadCycle_WorkerExitsUnexpectedly_RestartsFromLastShadowCopy()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var shadow = new FakeShadowCopyManager { LatestShadowCopy = @"C:\shadow\lastgood" };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, shadow: shadow, process: process))
            {
                orchestrator.Start();

                // Simulate worker exiting unexpectedly
                process.RaiseExited();

                // Wait for restart (launch from last shadow copy)
                Assert.True(WaitFor(() => process.LaunchedPaths.Count == 1, 3000));

                Assert.Equal(1, process.LaunchedPaths.Count);
                Assert.Equal(@"C:\shadow\lastgood\project.exe", process.LaunchedPaths[0]);
                Assert.Equal(ReloadState.Idle, orchestrator.State);
            }
        }

        // 4.4: Worker exits when no shadow copy available — no restart
        [Fact]
        public void ReloadCycle_WorkerExitsNoShadowCopy_NoRestart()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var shadow = new FakeShadowCopyManager { LatestShadowCopy = null };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, shadow: shadow, process: process))
            {
                orchestrator.Start();

                process.RaiseExited();

                // Give it time to (not) restart
                Assert.True(WaitFor(() => orchestrator.State == ReloadState.Idle, 3000));

                Assert.Equal(0, process.LaunchedPaths.Count);
                Assert.Equal(ReloadState.Idle, orchestrator.State);
            }
        }

        // Additional: StateChanged event fires during reload cycle
        [Fact]
        public void ReloadCycle_StateChanges_RaiseEvents()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var build = new FakeBuildOrchestrator { ShouldSucceed = false };
            var process = new FakeProcessManager();

            var states = new List<ReloadState>();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, process: process))
            {
                orchestrator.StateChanged += (s, e) => states.Add(e.NewState);
                orchestrator.Start();
                watcher.RaiseFileChanged();

                Thread.Sleep(500);

                Assert.Contains(ReloadState.Building, states);
                Assert.Contains(ReloadState.Failed, states);
            }
        }

        // Additional: IsReloading returns true during build
        [Fact]
        public void IsReloading_DuringBuild_ReturnsTrue()
        {
            var config = CreateConfig();
            var watcher = new FakeFileWatcherService();
            var build = new FakeBuildOrchestrator { ShouldSucceed = true, DelayMilliseconds = 500 };
            var process = new FakeProcessManager();

            using (var orchestrator = CreateOrchestrator(config, watcher, build, process: process))
            {
                orchestrator.Start();
                watcher.RaiseFileChanged();

                Thread.Sleep(100); // Let the build start

                Assert.True(orchestrator.IsReloading);

                Thread.Sleep(800); // Wait for completion

                Assert.False(orchestrator.IsReloading);
            }
        }
    }
}
