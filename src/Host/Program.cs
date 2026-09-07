using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HotReloadTool.Common;
using HotReloadTool.Contracts;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Host CLI entry point for the Hot Reload tool. Parses command-line arguments,
    /// validates configuration, instantiates all services, and starts the
    /// <see cref="ReloadOrchestrator"/> to monitor and hot-reload the worker.
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            CommandLineArgs parsed = CommandLineArgs.Parse(args);

            if (parsed.Command == HotReloadCommand.Help || parsed.Errors.Count > 0)
            {
                PrintHelp();
                foreach (string error in parsed.Errors)
                    Console.Error.WriteLine("ERROR: " + error);
                return parsed.Errors.Count > 0 ? 1 : 0;
            }

            switch (parsed.Command)
            {
                case HotReloadCommand.Start:
                    return parsed.Watch ? ExecuteWatch(parsed) : ExecuteStart(parsed);
                case HotReloadCommand.Stop:
                    return ExecuteStop(parsed);
                case HotReloadCommand.Status:
                    return ExecuteStatus(parsed);
                default:
                    PrintHelp();
                    return 1;
            }
        }

        static CliCommandServer _cliServer;

        static int ExecuteStart(CommandLineArgs args)
        {
            try
            {
                var config = new ReloadConfiguration
                {
                    ProjectPath = args.ProjectPath,
                    DebounceMilliseconds = args.DebounceMilliseconds ?? 500,
                    DrainTimeoutMilliseconds = args.DrainTimeoutMilliseconds ?? 3000,
                    StateTransferTimeoutMilliseconds = 5000,
                    PipeName = args.PipeName ?? Contracts.ReloadMessages.DefaultPipeName,
                    RetainedShadowCopies = 3
                };

                config.Validate();

                // MSBuild compatibility validation (Phase 1 task 1.7)
                MsBuildCompatibilityValidator.ValidateRuntime();

                // Instantiate all services
                var fileWatcher = new FileWatcherService(config);
                var buildOrchestrator = new BuildOrchestrator();
                var shadowCopyManager = new ShadowCopyManager(
                    ComputeProjectGuid(config.ProjectPath),
                    config.RetainedShadowCopies);
                var drainCoordinator = new DrainCoordinator(config.PipeName);
                var stateTransferHost = new StateTransferHost(config.PipeName, config.StateTransferTimeoutMilliseconds);
                var processManager = new ProcessManager();

                // Create the orchestrator
                var orchestrator = new ReloadOrchestrator(
                    config,
                    fileWatcher,
                    buildOrchestrator,
                    shadowCopyManager,
                    drainCoordinator,
                    stateTransferHost,
                    processManager);

                // Subscribe to state changes for console logging
                orchestrator.StateChanged += (sender, e) =>
                {
                    Console.WriteLine("[{0:HH:mm:ss}] State: {1} -> {2}",
                        DateTime.Now, e.OldState, e.NewState);
                };

                // Start the CLI command server for external stop/status
                _cliServer = new CliCommandServer(config.PipeName + "-cli", request =>
                {
                    if (request.Command == CliCommands.Stop)
                    {
                        // Signal shutdown on a background thread (avoid deadlock with pipe)
                        ThreadPool.QueueUserWorkItem(_ => _shutdownEvent.Set());
                        return new CliResponseMessage { Success = true };
                    }
                    if (request.Command == CliCommands.Status)
                    {
                        return new CliResponseMessage
                        {
                            Success = true,
                            State = orchestrator.State.ToString(),
                            IsReloading = orchestrator.IsReloading,
                            WorkerIsRunning = processManager.IsRunning,
                            ProjectPath = config.ProjectPath
                        };
                    }
                    return new CliResponseMessage { Success = false, Error = "Unknown command: " + request.Command };
                });
                _cliServer.Start();

                Console.WriteLine("Hot Reload Host starting...");
                Console.WriteLine("  Project: " + config.ProjectPath);
                Console.WriteLine("  Watch:   " + config.GetEffectiveWatchDirectory());
                Console.WriteLine("  Debounce: " + config.DebounceMilliseconds + "ms");
                Console.WriteLine("  Drain timeout: " + config.DrainTimeoutMilliseconds + "ms");
                Console.WriteLine("  Pipe: " + config.PipeName);
                Console.WriteLine("Press Ctrl+C or Enter to stop.");

                // Start the orchestrator
                orchestrator.Start();

                // Handle Ctrl+C for graceful shutdown
                _shutdownEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    _shutdownEvent.Set();
                };

                // Wait for Ctrl+C, Enter, or external stop command
                while (!_shutdownEvent.WaitOne(0))
                {
                    try
                    {
                        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                            break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Console input is redirected or unavailable — just wait on the event
                    }
                    Thread.Sleep(100);
                }

                Console.WriteLine("Shutting down...");
                _cliServer.Stop();
                _cliServer.Dispose();
                orchestrator.Stop();
                orchestrator.Dispose();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to start: " + ex.Message);
                return 1;
            }
        }

        static ManualResetEvent _shutdownEvent;

        /// <summary>
        /// Runs the Host in watch mode: monitors the src/ directory for .cs changes,
        /// rebuilds on change, and restarts the host process.
        /// </summary>
        static int ExecuteWatch(CommandLineArgs args)
        {
            // BaseDirectory is src\Host\bin\Debug\ -> go up to solution root
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string srcDir = Path.Combine(baseDir, "..", "..", "..", "src");
            srcDir = Path.GetFullPath(srcDir);
            string slnPath = Path.Combine(baseDir, "..", "..", "..", "HotReloadTool.sln");
            slnPath = Path.GetFullPath(slnPath);
            string exePath = Process.GetCurrentProcess().MainModule.FileName;

            Console.WriteLine("[netman watch] Watching: " + srcDir);
            Console.WriteLine("[netman watch] Press Ctrl+C to stop.");
            Console.WriteLine("");

            BuildSolution(slnPath);

            var startArgs = new System.Collections.Generic.List<string> { "start" };
            if (!string.IsNullOrEmpty(args.ProjectPath))
            {
                startArgs.Add("-p");
                startArgs.Add(args.ProjectPath);
            }
            if (args.DebounceMilliseconds.HasValue)
            {
                startArgs.Add("--debounce");
                startArgs.Add(args.DebounceMilliseconds.Value.ToString());
            }
            if (args.DrainTimeoutMilliseconds.HasValue)
            {
                startArgs.Add("--drain-timeout");
                startArgs.Add(args.DrainTimeoutMilliseconds.Value.ToString());
            }
            if (!string.IsNullOrEmpty(args.PipeName))
            {
                startArgs.Add("--pipe-name");
                startArgs.Add(args.PipeName);
            }

            _watchHostProcess = StartHost(exePath, startArgs.ToArray());
            _watchDebounceTimer = null;
            _watchSlnPath = slnPath;
            _watchExePath = exePath;
            _watchStartArgs = startArgs.ToArray();

            var watcher = new FileSystemWatcher(srcDir, "*.cs")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Changed += (sender, e) => ScheduleWatchReload();
            watcher.Created += (sender, e) => ScheduleWatchReload();
            watcher.Renamed += (sender, e) => ScheduleWatchReload();

            var shutdown = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                shutdown.Set();
            };

            shutdown.WaitOne();

            watcher.Dispose();
            if (_watchHostProcess != null && !_watchHostProcess.HasExited)
            {
                _watchHostProcess.Kill();
                _watchHostProcess.WaitForExit(5000);
            }

            Console.WriteLine("[netman watch] Stopped.");
            return 0;
        }

        static Process _watchHostProcess;
        static System.Timers.Timer _watchDebounceTimer;
        static string _watchSlnPath;
        static string _watchExePath;
        static string[] _watchStartArgs;
        static object _watchReloadLock = new object();

        static void ScheduleWatchReload()
        {
            lock (_watchReloadLock)
            {
                if (_watchDebounceTimer != null)
                {
                    _watchDebounceTimer.Stop();
                    _watchDebounceTimer.Dispose();
                }
                _watchDebounceTimer = new System.Timers.Timer(1500);
                _watchDebounceTimer.AutoReset = false;
                _watchDebounceTimer.Elapsed += (s, evt) =>
                {
                    Console.WriteLine("");
                    Console.WriteLine("[netman watch] Change detected: rebuilding...");
                    if (_watchHostProcess != null && !_watchHostProcess.HasExited)
                    {
                        _watchHostProcess.Kill();
                        _watchHostProcess.WaitForExit(5000);
                    }
                    BuildSolution(_watchSlnPath);
                    _watchHostProcess = StartHost(_watchExePath, _watchStartArgs);
                };
                _watchDebounceTimer.Start();
            }
        }

        static Process StartHost(string exePath, string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            return Process.Start(startInfo);
        }

        static void BuildSolution(string slnPath)
        {
            try
            {
                var buildInfo = new ProcessStartInfo
                {
                    FileName = "msbuild",
                    Arguments = "\"" + slnPath + "\" /p:Configuration=Debug /nologo /v:quiet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var build = Process.Start(buildInfo))
                {
                    build.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[netman watch] Build failed: " + ex.Message);
            }
        }

        static int ExecuteStop(CommandLineArgs args)
        {
            string pipeName = args.PipeName ?? Contracts.ReloadMessages.DefaultPipeName;

            using (var client = new CliCommandClient(pipeName + "-cli"))
            {
                CliResponseMessage response = client.SendCommand(CliCommands.Stop, timeoutMilliseconds: 5000);

                if (response == null)
                {
                    Console.Error.WriteLine("No running host found for pipe: " + pipeName);
                    return 1;
                }

                if (response.Success)
                {
                    Console.WriteLine("Stop signal sent. Host is shutting down.");
                    return 0;
                }

                Console.Error.WriteLine("Stop failed: " + response.Error);
                return 1;
            }
        }

        static int ExecuteStatus(CommandLineArgs args)
        {
            string pipeName = args.PipeName ?? Contracts.ReloadMessages.DefaultPipeName;

            using (var client = new CliCommandClient(pipeName + "-cli"))
            {
                CliResponseMessage response = client.SendCommand(CliCommands.Status, timeoutMilliseconds: 3000);

                if (response == null)
                {
                    Console.Error.WriteLine("No running host found for pipe: " + pipeName);
                    return 1;
                }

                if (response.Success)
                {
                    Console.WriteLine("Hot Reload Host Status");
                    Console.WriteLine("  Project:    " + response.ProjectPath);
                    Console.WriteLine("  State:      " + response.State);
                    Console.WriteLine("  Reloading:  " + response.IsReloading);
                    Console.WriteLine("  Worker:     " + (response.WorkerIsRunning ? "running" : "stopped"));
                    return 0;
                }

                Console.Error.WriteLine("Status failed: " + response.Error);
                return 1;
            }
        }

        /// <summary>
        /// Computes a stable GUID-like string from the project path for use as
        /// the shadow-copy namespace identifier.
        /// </summary>
        private static string ComputeProjectGuid(string projectPath)
        {
            // Use a hash of the full path to create a stable, filesystem-safe identifier
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath.ToUpperInvariant()));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "");
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine("Hot Reload Tool for .NET Framework Microservices");
            Console.WriteLine();
            Console.WriteLine("Usage: HotReloadTool.Host <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  start     Begin monitoring and hot-reload the worker project.");
            Console.WriteLine("  stop      Stop the running hot-reload session.");
            Console.WriteLine("  status    Print the current status of the worker and reload cycle.");
            Console.WriteLine("  help      Print this help text.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --project, -p <path>   Path to the worker .csproj (required for start).");
            Console.WriteLine("  --debounce <ms>        Debounce interval (default: 500).");
            Console.WriteLine("  --drain-timeout <ms>   Connection drain timeout (default: 3000).");
            Console.WriteLine("  --pipe-name <name>     Named Pipes endpoint (default: hotreload-state).");
            Console.WriteLine("  --watch, -w            Watch src/ for .cs changes and auto-rebuild.");
        }
    }
}
