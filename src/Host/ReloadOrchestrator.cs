using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HotReloadTool.Common;
using HotReloadTool.Contracts;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Abstraction for building a worker project. Implemented by
    /// <see cref="BuildOrchestrator"/>; allows test doubles to be injected.
    /// </summary>
    public interface IBuildOrchestrator
    {
        /// <summary>
        /// Builds the specified project file.
        /// </summary>
        /// <param name="projectPath">Absolute path to a .csproj file.</param>
        /// <returns>A <see cref="BuildResult"/> indicating success or failure.</returns>
        BuildResult Build(string projectPath);
    }

    /// <summary>
    /// Abstraction for shadow-copy management. Implemented by
    /// <see cref="ShadowCopyManager"/>; allows test doubles to be injected.
    /// </summary>
    public interface IShadowCopyManager
    {
        /// <summary>
        /// Copies all files from the build output directory into a new
        /// timestamped shadow directory.
        /// </summary>
        /// <param name="buildOutputPath">Absolute path to the build output directory.</param>
        /// <returns>Absolute path to the new shadow copy directory.</returns>
        string CopyToTemp(string buildOutputPath);

        /// <summary>
        /// Removes shadow copy directories older than the most recent retain count.
        /// </summary>
        void CleanupOldCopies();

        /// <summary>
        /// Returns the most recently created shadow copy directory, or <c>null</c>.
        /// </summary>
        string GetLatestShadowCopy();
    }

    /// <summary>
    /// Abstraction for connection draining. Implemented by
    /// <see cref="DrainCoordinator"/>; allows test doubles to be injected.
    /// </summary>
    public interface IDrainCoordinator
    {
        /// <summary>
        /// Sends a drain request to the worker and waits for acknowledgment.
        /// </summary>
        /// <param name="timeoutMilliseconds">Maximum time to wait.</param>
        /// <returns><c>true</c> if drain was acknowledged; <c>false</c> on timeout or error.</returns>
        bool Drain(int timeoutMilliseconds);
    }

    /// <summary>
    /// Abstraction for state transfer. Implemented by
    /// <see cref="StateTransferHost"/>; allows test doubles to be injected.
    /// </summary>
    public interface IStateTransferHost : IDisposable
    {
        /// <summary>
        /// Begins listening for the old worker to push its state.
        /// </summary>
        /// <param name="timeoutMilliseconds">Maximum time to wait.</param>
        /// <returns>The state JSON string, or <c>null</c> if timed out.</returns>
        string BeginReceive(int timeoutMilliseconds = 0);

        /// <summary>
        /// Delivers the held state to the new worker.
        /// </summary>
        /// <param name="timeoutMilliseconds">Maximum time to wait.</param>
        /// <returns><c>true</c> if the state was delivered successfully.</returns>
        bool DeliverState(int timeoutMilliseconds = 0);

        /// <summary>
        /// Gets the currently held state JSON, or <c>null</c>.
        /// </summary>
        string HeldState { get; }
    }

    /// <summary>
    /// Abstraction for worker process management. Implemented by
    /// <see cref="ProcessManager"/>; allows test doubles to be injected.
    /// </summary>
    public interface IProcessManager : IDisposable
    {
        /// <summary>
        /// The underlying <see cref="Process"/> instance, or <c>null</c>.
        /// </summary>
        Process Current { get; }

        /// <summary>
        /// <c>true</c> when a worker process is running and has not exited.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Launches a new worker process.
        /// </summary>
        /// <param name="executablePath">Absolute path to the worker .exe.</param>
        /// <param name="arguments">Optional command-line arguments.</param>
        void Launch(string executablePath, string arguments = null);

        /// <summary>
        /// Attempts graceful termination with Kill fallback.
        /// </summary>
        /// <param name="timeoutMilliseconds">Time to wait before Kill.</param>
        /// <returns><c>true</c> if the worker exited gracefully.</returns>
        bool TerminateGracefully(int timeoutMilliseconds);

        /// <summary>
        /// Forcefully terminates the worker process.
        /// </summary>
        void KillWorker();

        /// <summary>
        /// Raised when the worker process exits.
        /// </summary>
        event EventHandler Exited;
    }

    /// <summary>
    /// Event arguments for the <see cref="ReloadOrchestrator.StateChanged"/> event.
    /// </summary>
    public class ReloadStateChangedEventArgs : EventArgs
    {
        /// <summary>The previous orchestration state.</summary>
        public ReloadState OldState { get; private set; }

        /// <summary>The new orchestration state.</summary>
        public ReloadState NewState { get; private set; }

        /// <summary>
        /// Creates a new <see cref="ReloadStateChangedEventArgs"/>.
        /// </summary>
        /// <param name="oldState">The previous state.</param>
        /// <param name="newState">The new state.</param>
        public ReloadStateChangedEventArgs(ReloadState oldState, ReloadState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Orchestrates the hot-reload cycle: Watch → Debounce → Build → ShadowCopy →
    /// Drain → StateTransfer → Terminate → Launch. Implements a thread-safe state
    /// machine using the <see cref="ReloadState"/> enum. Only one reload cycle runs
    /// at a time; additional file changes during a cycle are coalesced into a single
    /// queued reload.
    /// </summary>
    /// <remarks>
    /// Thread safety: all state transitions are protected by <see cref="_sync"/>.
    /// The reload cycle runs on a thread-pool thread so the file-watcher callback
    /// is never blocked. Queue-one semantics: if a file change arrives while a
    /// reload is in progress, exactly one additional reload is queued to run after
    /// the current cycle completes.
    /// </remarks>
    public class ReloadOrchestrator : IDisposable
    {
        private readonly ReloadConfiguration _config;
        private readonly IFileWatcherService _fileWatcher;
        private readonly IBuildOrchestrator _buildOrchestrator;
        private readonly IShadowCopyManager _shadowCopyManager;
        private readonly IDrainCoordinator _drainCoordinator;
        private readonly IStateTransferHost _stateTransferHost;
        private readonly IProcessManager _processManager;

        private ReloadState _state;
        private readonly object _sync = new object();
        private bool _queued;
        private bool _disposed;

        /// <summary>
        /// Creates a new <see cref="ReloadOrchestrator"/>.
        /// </summary>
        /// <param name="config">Configuration with all settings.</param>
        /// <param name="fileWatcher">File watcher service.</param>
        /// <param name="buildOrchestrator">Build service.</param>
        /// <param name="shadowCopyManager">Shadow-copy service.</param>
        /// <param name="drainCoordinator">Drain service.</param>
        /// <param name="stateTransferHost">State transfer service.</param>
        /// <param name="processManager">Process management service.</param>
        public ReloadOrchestrator(
            ReloadConfiguration config,
            IFileWatcherService fileWatcher,
            IBuildOrchestrator buildOrchestrator,
            IShadowCopyManager shadowCopyManager,
            IDrainCoordinator drainCoordinator,
            IStateTransferHost stateTransferHost,
            IProcessManager processManager)
        {
            _config = config;
            _fileWatcher = fileWatcher;
            _buildOrchestrator = buildOrchestrator;
            _shadowCopyManager = shadowCopyManager;
            _drainCoordinator = drainCoordinator;
            _stateTransferHost = stateTransferHost;
            _processManager = processManager;
            _state = ReloadState.Idle;
        }

        /// <summary>
        /// Gets the current orchestration state.
        /// </summary>
        public ReloadState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        /// <summary>
        /// Gets whether a reload cycle is currently in progress.
        /// </summary>
        public bool IsReloading
        {
            get
            {
                lock (_sync)
                {
                    return _state != ReloadState.Idle && _state != ReloadState.Failed;
                }
            }
        }

        /// <summary>
        /// Raised when the orchestration state changes.
        /// </summary>
        public event EventHandler<ReloadStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Starts the file watcher and subscribes to process exit events.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException("ReloadOrchestrator");

                _fileWatcher.FileChanged += OnFileChanged;
                _fileWatcher.Error += OnFileWatcherError;
                _processManager.Exited += OnProcessExited;
                _fileWatcher.Start();
            }
        }

        /// <summary>
        /// Stops the file watcher and unsubscribes from events.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _fileWatcher.Stop();
                _fileWatcher.FileChanged -= OnFileChanged;
                _fileWatcher.Error -= OnFileWatcherError;
                _processManager.Exited -= OnProcessExited;
            }
        }

        private void OnFileChanged(object sender, FileChangedEventArgs e)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                if (IsReloading)
                {
                    // Queue one reload — no concurrent builds allowed
                    _queued = true;
                    return;
                }

                // Start reload cycle on thread-pool thread
                ThreadPool.QueueUserWorkItem(_ => ExecuteReloadCore());
            }
        }

        private void ExecuteReloadCore()
        {
            try
            {
                // Build phase
                SetState(ReloadState.Building);
                BuildResult buildResult = _buildOrchestrator.Build(_config.ProjectPath);

                if (!buildResult.Success)
                {
                    // Build failed — keep old worker running, await next change
                    SetState(ReloadState.Failed);
                    return;
                }

                // Shadow copy phase
                SetState(ReloadState.ShadowCopying);
                string buildOutputPath = GetBuildOutputPath();
                string shadowPath = _shadowCopyManager.CopyToTemp(buildOutputPath);

                // Drain phase
                SetState(ReloadState.Draining);
                _drainCoordinator.Drain(_config.DrainTimeoutMilliseconds);

                // State transfer phase
                SetState(ReloadState.TransferringState);
                string state = _stateTransferHost.BeginReceive(_config.StateTransferTimeoutMilliseconds);

                // Terminate old worker
                SetState(ReloadState.TerminatingOld);
                _processManager.TerminateGracefully(_config.DrainTimeoutMilliseconds);

                // Launch new worker
                SetState(ReloadState.LaunchingNew);
                string executablePath = GetWorkerExecutablePath(shadowPath);
                if (executablePath == null)
                {
                    SetState(ReloadState.Failed);
                    return;
                }
                _processManager.Launch(executablePath);

                // Deliver state to new worker
                if (state != null)
                {
                    _stateTransferHost.DeliverState(_config.StateTransferTimeoutMilliseconds);
                }

                // Cleanup old shadow copies
                _shadowCopyManager.CleanupOldCopies();

                // Return to idle
                SetState(ReloadState.Idle);

                // Check if a reload was queued during the cycle
                lock (_sync)
                {
                    if (_queued)
                    {
                        _queued = false;
                        ThreadPool.QueueUserWorkItem(_ => ExecuteReloadCore());
                    }
                }
            }
            catch (Exception)
            {
                SetState(ReloadState.Failed);
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (IsReloading)
                    return; // Normal termination during reload cycle

                // Worker exited unexpectedly — attempt restart from last known good
                string lastShadow = _shadowCopyManager.GetLatestShadowCopy();
                if (lastShadow != null)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            SetState(ReloadState.LaunchingNew);
                            string executablePath = GetWorkerExecutablePath(lastShadow);
                            if (executablePath != null)
                            {
                                _processManager.Launch(executablePath);
                            }
                            SetState(ReloadState.Idle);
                        }
                        catch (Exception)
                        {
                            SetState(ReloadState.Failed);
                        }
                    });
                }
            }
        }

        private void OnFileWatcherError(object sender, ErrorEventArgs e)
        {
            // Errors are non-fatal; continue watching.
            // Subscribers can observe via the Error event if needed.
        }

        private string GetBuildOutputPath()
        {
            string projectDir = Path.GetDirectoryName(Path.GetFullPath(_config.ProjectPath));
            return Path.Combine(projectDir, "bin", "Debug");
        }

        private string GetWorkerExecutablePath(string shadowPath)
        {
            if (string.IsNullOrEmpty(shadowPath))
                return null;

            // Try project-name.exe first
            string projectName = Path.GetFileNameWithoutExtension(_config.ProjectPath);
            string exePath = Path.Combine(shadowPath, projectName + ".exe");
            return exePath;
        }

        private void SetState(ReloadState newState)
        {
            ReloadState oldState;
            lock (_sync)
            {
                oldState = _state;
                _state = newState;
            }
            OnStateChanged(oldState, newState);
        }

        private void OnStateChanged(ReloadState oldState, ReloadState newState)
        {
            var handler = StateChanged;
            if (handler != null)
                handler(this, new ReloadStateChangedEventArgs(oldState, newState));
        }

        /// <summary>
        /// Releases resources used by the orchestrator.
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;

                Stop();
                _fileWatcher.Dispose();
                _stateTransferHost.Dispose();
                _processManager.Dispose();
            }
        }
    }
}
