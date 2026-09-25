using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// In <see cref="ReloadMode.BuildRecycle"/> mode the cycle is
    /// Building → Deploying → Recycling → Idle: shadow-copy, drain, state
    /// transfer, and worker relaunch are skipped; the deploy step runs
    /// <see cref="LibraryDeployer"/>, and the recycle step runs
    /// <see cref="AppDomainRecycler"/> to touch the recycle target.
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
        private readonly LibraryDeployer _libraryDeployer = new LibraryDeployer();
        private readonly AppDomainRecycler _appDomainRecycler = new AppDomainRecycler();
        private readonly string _solutionProjectName;
        private readonly IFileWatcherService _webWatcher;
        private readonly bool _hasWebWatch;

        private ReloadState _state;
        private readonly object _sync = new object();
        private bool _queued;
        private bool _webQueued;
        private bool _disposed;

        /// <summary>
        /// Web files changed since the last web-only recycle started. Cleared
        /// each time <see cref="ExecuteWebReloadCore"/> begins, so it reports
        /// only the changes that triggered (or piled up ahead of) that cycle.
        /// </summary>
        private readonly List<string> _pendingWebChangedPaths = new List<string>();

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
            _solutionProjectName = config != null ? config.SolutionProjectName : null;
            _state = ReloadState.Idle;

            // Optional second watcher for web files (.aspx/.aspx.vb). Changes
            // here skip the library build/deploy and go straight to recycle.
            if (config != null && !string.IsNullOrWhiteSpace(config.WebWatchDirectory))
            {
                var webConfig = new ReloadConfiguration
                {
                    WatchDirectory = config.WebWatchDirectory,
                    Extensions = config.WebExtensions,
                    ExclusionPatterns = config.ExclusionPatterns,
                    DebounceMilliseconds = config.DebounceMilliseconds
                };
                _webWatcher = new FileWatcherService(webConfig);
                _hasWebWatch = true;
            }
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

                if (_hasWebWatch)
                {
                    _webWatcher.FileChanged += OnWebFileChanged;
                    _webWatcher.Error += OnFileWatcherError;
                    _webWatcher.Start();
                }
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

                if (_hasWebWatch)
                {
                    _webWatcher.Stop();
                    _webWatcher.FileChanged -= OnWebFileChanged;
                    _webWatcher.Error -= OnFileWatcherError;
                }
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

        private void OnWebFileChanged(object sender, FileChangedEventArgs e)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                if (e != null && e.Paths != null)
                    _pendingWebChangedPaths.AddRange(e.Paths);

                if (IsReloading)
                {
                    // Queue one web-only recycle — no concurrent cycles allowed
                    _webQueued = true;
                    return;
                }

                // Start a recycle-only cycle (no library build/deploy) on a
                // thread-pool thread.
                _webQueued = true;
                ThreadPool.QueueUserWorkItem(_ => ExecuteWebReloadCore());
            }
        }

        /// <summary>
        /// Executes a recycle-only cycle for web-file changes. Skips the
        /// library build/deploy phases — only touches the recycle target so
        /// ASP.NET recompiles the changed pages and recycles the AppDomain.
        /// Mirrors the build-recycle mode's settle semantics: on failure it
        /// goes to <see cref="ReloadState.Failed"/> and the queued-reload
        /// check in the caller still runs.
        /// </summary>
        private void ExecuteWebReloadCore()
        {
            List<string> changedPaths;
            lock (_sync)
            {
                changedPaths = new List<string>(_pendingWebChangedPaths);
                _pendingWebChangedPaths.Clear();
            }

            try
            {
                // Web-file changes (.aspx/.aspx.vb) recompile per-page in ASP.NET
                // dynamic compilation. Touching web.config would recycle the whole
                // AppDomain, wiping the in-proc Session/Application (module context)
                // and forcing a re-login. So we do NOT recycle here: we only bump a
                // stamp file the browser polls to reload the current page in place.
                SetState(ReloadState.Recycling);
                BumpStamp("web-reload.stamp");
                SetState(ReloadState.Idle);
                PrintChangedPageUrls(changedPaths);
            }
            catch (Exception)
            {
                SetState(ReloadState.Failed);
            }
            finally
            {
                // Check if another reload (full or web-only) was queued during
                // the cycle. A full library change takes priority.
                ProcessQueuedReloads();
            }
        }

        /// <summary>
        /// Writes/updates a small stamp file in the tool's output directory so an
        /// external watcher (the browser automation) can detect reload vs recycle
        /// events without touching web.config. Never throws.
        /// </summary>
        private static void BumpStamp(string fileName)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                File.WriteAllText(p, DateTime.Now.ToString("o"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("WARNING: could not write stamp '" + fileName + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Prints the direct URL of each changed page, when
        /// <see cref="ReloadConfiguration.SiteBaseUrl"/> is configured. No-op
        /// (silent) when it isn't set, or when none of the changed paths fall
        /// under <see cref="ReloadConfiguration.WebWatchDirectory"/>.
        /// </summary>
        private void PrintChangedPageUrls(List<string> changedPaths)
        {
            if (string.IsNullOrWhiteSpace(_config.SiteBaseUrl) || changedPaths == null || changedPaths.Count == 0)
                return;

            var urls = changedPaths
                .Select(BuildPageUrl)
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
                return;

            // Print the direct page URL plus an autologin link that auto-logins
            // and redirects to the page. Copy-paste either into the browser.
            string autoBase = _config.SiteBaseUrl.TrimEnd('/') + "/_netman-autologin.html?to=";
            if (urls.Count == 1)
            {
                Console.WriteLine("  URL: " + urls[0]);
                Console.WriteLine("  OPEN: " + autoBase + Uri.EscapeDataString(urls[0]));
            }
            else
            {
                Console.WriteLine("  URLs:");
                foreach (string url in urls)
                {
                    Console.WriteLine("    " + url);
                    Console.WriteLine("    OPEN: " + autoBase + Uri.EscapeDataString(url));
                }
            }
        }

        /// <summary>
        /// Maps a changed file under <see cref="ReloadConfiguration.WebWatchDirectory"/>
        /// to the page's URL under <see cref="ReloadConfiguration.SiteBaseUrl"/>.
        /// A code-behind change (<c>.aspx.vb</c>/<c>.aspx.cs</c>) is mapped to
        /// its <c>.aspx</c> page. Returns <c>null</c> when the path isn't under
        /// the web-watch directory.
        /// </summary>
        internal string BuildPageUrl(string changedPath)
        {
            if (string.IsNullOrWhiteSpace(changedPath) || string.IsNullOrWhiteSpace(_config.WebWatchDirectory))
                return null;

            string watchDir;
            string fullPath;
            try
            {
                watchDir = Path.GetFullPath(_config.WebWatchDirectory).TrimEnd('\\', '/');
                fullPath = Path.GetFullPath(changedPath);
            }
            catch
            {
                return null;
            }

            if (!fullPath.StartsWith(watchDir, StringComparison.OrdinalIgnoreCase))
                return null;

            string relative = fullPath.Substring(watchDir.Length).TrimStart('\\', '/');

            // A code-behind change recycles the page itself — point at the .aspx.
            if (relative.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(0, relative.Length - ".vb".Length);
            else if (relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(0, relative.Length - ".cs".Length);

            string[] segments = relative.Split('\\', '/');
            string encodedRelative = string.Join("/", segments.Select(Uri.EscapeDataString));

            string baseUrl = _config.SiteBaseUrl.TrimEnd('/') + "/";
            return baseUrl + encodedRelative;
        }

        /// <summary>
        /// Drains the queued-reload flags and dispatches the next cycle on the
        /// thread pool. A queued full library reload takes priority over a
        /// queued web-only recycle; when neither is queued this is a no-op.
        /// Must be called from inside or after the reload cycle (the caller
        /// must NOT hold <see cref="_sync"/>).
        /// </summary>
        private void ProcessQueuedReloads()
        {
            lock (_sync)
            {
                if (_queued)
                {
                    _queued = false;
                    ThreadPool.QueueUserWorkItem(_ => ExecuteReloadCore());
                }
                else if (_webQueued)
                {
                    _webQueued = false;
                    ThreadPool.QueueUserWorkItem(_ => ExecuteWebReloadCore());
                }
            }
        }

        private void ExecuteReloadCore()
        {
            try
            {
                // Build phase
                SetState(ReloadState.Building);
                BuildResult buildResult = BuildWithSolutionProject();

                if (!buildResult.Success)
                {
                    // Build failed — keep old worker running, await next change
                    SetState(ReloadState.Failed);
                    return;
                }

                // Build-recycle mode: deploy to the web site and recycle the
                // AppDomain instead of relaunching a worker process.
                if (_config.Mode == ReloadMode.BuildRecycle)
                {
                    ExecuteBuildRecycleCycle(buildResult);

                    // Check if another reload was queued during the cycle
                    ProcessQueuedReloads();
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

                // Check if another reload was queued during the cycle
                ProcessQueuedReloads();
            }
            catch (Exception)
            {
                SetState(ReloadState.Failed);
            }
        }

        /// <summary>
        /// Executes the build-recycle cycle after a successful build:
        /// Deploying → Recycling → Idle. The deploy step copies the build's
        /// <see cref="BuildResult.OutputAssemblyPath"/> into
        /// <see cref="ReloadConfiguration.DeployToDirectory"/> via
        /// <see cref="LibraryDeployer"/>; the recycle step touches
        /// <see cref="ReloadConfiguration.RecycleTargetPath"/> via
        /// <see cref="AppDomainRecycler"/> so ASP.NET recycles the AppDomain.
        /// On deploy or recycle failure the cycle logs the error and
        /// settles to <see cref="ReloadState.Failed"/> — mirroring the
        /// build-failure gate (both pass through the queued-reload check
        /// in the caller).
        /// </summary>
        /// <param name="buildResult">The successful build whose output is deployed.</param>
        private void ExecuteBuildRecycleCycle(BuildResult buildResult)
        {
            // Deploy phase — copy the build output into _config.DeployToDirectory.
            SetState(ReloadState.Deploying);

            DeployResult deployResult;
            if (string.IsNullOrWhiteSpace(buildResult.OutputAssemblyPath))
            {
                deployResult = DeployResult.Failed("Build produced no output assembly path.", 0);
            }
            else
            {
                deployResult = _libraryDeployer.Deploy(
                    buildResult.OutputAssemblyPath,
                    _config.DeployToDirectory,
                    _config.DeployPdb);
            }

            if (!deployResult.Success)
            {
                // Deploy failed — mirror the build-failure gate: keep the old
                // target running, skip recycling, await the next change.
                Console.Error.WriteLine("ERROR: Deploy failed: " + deployResult.Error);
                SetState(ReloadState.Failed);
                return;
            }

            // Fix up the .refresh pointer (if any) so ASP.NET's own
            // AppDomain recycle copies from the real build output, not from a
            // stale/wrong path. Without this, ASP.NET would re-copy a DLL from
            // a path resolved relative to the deploy dir and overwrite the one
            // we just deployed.
            RefreshPointerFix(deployResult.DestinationPath, buildResult.OutputAssemblyPath);

            // Recycle phase — touch _config.RecycleTargetPath so ASP.NET's
            // FileSystemWatcher restarts the AppDomain (loading the deployed DLL).
            SetState(ReloadState.Recycling);

            RecycleResult recycleResult = _appDomainRecycler.Recycle(_config.RecycleTargetPath);

            if (!recycleResult.Success)
            {
                // Recycle failed — mirror the deploy-failure gate: log the
                // error, skip Idle, settle to Failed (the queued-reload check
                // in the caller still runs).
                Console.Error.WriteLine("ERROR: Recycle failed: " + recycleResult.Error);
                SetState(ReloadState.Failed);
                return;
            }

            // Library change did recycle the AppDomain — signal the browser so it
            // can re-login (session was wiped) instead of relying on a stale cookie.
            BumpStamp("recycle.stamp");

            // Return to watching
            SetState(ReloadState.Idle);
        }

        /// <summary>
        /// Rewrites any <c>.dll.refresh</c> pointer next to the deployed assembly
        /// so it points at the real build output (an absolute path). This
        /// prevents ASP.NET's AppDomain recycle from re-copying a stale DLL
        /// resolved from a <c>.refresh</c> path that is relative to the deploy
        /// directory — which would overwrite the freshly deployed assembly.
        /// </summary>
        /// <param name="deployedAssemblyPath">
        /// Absolute path of the assembly that was just deployed.
        /// </param>
        /// <param name="sourceAssemblyPath">
        /// Absolute path of the real build output to point the .refresh at.
        /// </param>
        private static void RefreshPointerFix(string deployedAssemblyPath, string sourceAssemblyPath)
        {
            if (string.IsNullOrWhiteSpace(deployedAssemblyPath))
                return;

            string refreshPath = deployedAssemblyPath + ".refresh";
            if (!File.Exists(refreshPath))
                return;

            try
            {
                File.WriteAllText(refreshPath, sourceAssemblyPath);
            }
            catch (Exception ex)
            {
                // Non-fatal: a failed .refresh update should not abort the
                // recycle cycle — the deployed DLL is already in place.
                Console.Error.WriteLine("WARNING: could not update .refresh pointer: " + ex.Message);
            }
        }

        /// <summary>
        /// Builds the configured project, passing the solution project name when
        /// present. The <see cref="IBuildOrchestrator"/> interface keeps a single
        /// Build(string) signature (unchanged), so we reach the optional second
        /// parameter on the concrete <see cref="BuildOrchestrator"/> via a safe
        /// runtime cast — this avoids changing the interface signature and keeps
        /// existing tests working.
        /// </summary>
        private BuildResult BuildWithSolutionProject()
        {
            if (!string.IsNullOrEmpty(_solutionProjectName))
            {
                var concrete = _buildOrchestrator as BuildOrchestrator;
                if (concrete != null)
                    return concrete.Build(_config.ProjectPath, _solutionProjectName);
            }
            return _buildOrchestrator.Build(_config.ProjectPath);
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
                if (_hasWebWatch)
                    _webWatcher.Dispose();
                _stateTransferHost.Dispose();
                _processManager.Dispose();
            }
        }
    }
}
