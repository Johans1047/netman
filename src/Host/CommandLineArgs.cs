using System;
using System.Collections.Generic;
using HotReloadTool.Common;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Supported CLI commands for the Hot Reload tool.
    /// </summary>
    public enum HotReloadCommand
    {
        /// <summary>Start monitoring and hot-reload the worker project.</summary>
        Start,

        /// <summary>Stop the running hot-reload session.</summary>
        Stop,

        /// <summary>Print the current status of the worker and reload cycle.</summary>
        Status,

        /// <summary>Print help text and exit.</summary>
        Help
    }

    /// <summary>
    /// Parsed command-line arguments for the Hot Reload tool.
    /// </summary>
    public class CommandLineArgs
    {
        /// <summary>The command to execute.</summary>
        public HotReloadCommand Command { get; set; }

        /// <summary>Path to the worker project (.csproj) or solution (.sln). Required for <c>start</c>.</summary>
        public string ProjectPath { get; set; }

        /// <summary>Override for debounce interval in milliseconds.</summary>
        public int? DebounceMilliseconds { get; set; }

        /// <summary>Override for drain timeout in milliseconds.</summary>
        public int? DrainTimeoutMilliseconds { get; set; }

        /// <summary>Override for Named Pipes endpoint name.</summary>
        public string PipeName { get; set; }

        /// <summary>Watch source files for changes and auto-rebuild.</summary>
        public bool Watch { get; set; }

        /// <summary>Reload mode. Default: <see cref="ReloadMode.Process"/>.</summary>
        public ReloadMode Mode { get; set; }

        /// <summary>
        /// Comma-separated watch extensions (e.g. <c>*.vb,*.config</c>), or
        /// <c>null</c> when not specified (defaults apply).
        /// </summary>
        public string ExtensionsCsv { get; set; }

        /// <summary>
        /// Directory that receives built output. Required for
        /// <see cref="ReloadMode.BuildRecycle"/>.
        /// </summary>
        public string DeployToDirectory { get; set; }

        /// <summary>
        /// File to touch so ASP.NET recycles the AppDomain. Required for
        /// <see cref="ReloadMode.BuildRecycle"/>.
        /// </summary>
        public string RecycleTargetPath { get; set; }

        /// <summary>
        /// Optional second directory to watch for web-file changes that only
        /// require an AppDomain recycle (no library rebuild).
        /// </summary>
        public string WebWatchDirectory { get; set; }

        /// <summary>
        /// Comma-separated web-watch extensions, or <c>null</c> for defaults.
        /// </summary>
        public string WebExtensionsCsv { get; set; }

        /// <summary>
        /// Base URL of the running site (e.g.
        /// <c>http://localhost:12345/SIPAF/</c>). When set together with
        /// <c>--web-watch</c>, netman prints the direct URL of the changed
        /// page after each web-file recycle.
        /// </summary>
        public string SiteUrl { get; set; }

        /// <summary>
        /// Project name within a solution whose output assembly to deploy.
        /// Required when <see cref="ProjectPath"/> is a .sln.
        /// </summary>
        public string SolutionProjectName { get; set; }

        /// <summary>
        /// When true (default), deploy the .pdb alongside the DLL in
        /// build-recycle mode. <c>--no-pdb</c> sets this to false.
        /// </summary>
        public bool DeployPdb { get; set; }

        /// <summary>Unparsed arguments that did not match known options.</summary>
        public List<string> UnrecognizedArgs { get; set; }

        /// <summary>Parsing errors encountered.</summary>
        public List<string> Errors { get; set; }

        public CommandLineArgs()
        {
            Mode = ReloadMode.Process;
            DeployPdb = true;
            UnrecognizedArgs = new List<string>();
            Errors = new List<string>();
        }

        /// <summary>
        /// Parses command-line arguments into a <see cref="CommandLineArgs"/>.
        /// </summary>
        /// <param name="args">Raw arguments (excluding process name).</param>
        /// <returns>Parsed arguments with any errors in <see cref="Errors"/>.</returns>
        public static CommandLineArgs Parse(string[] args)
        {
            var result = new CommandLineArgs();

            if (args == null || args.Length == 0)
            {
                result.Command = HotReloadCommand.Help;
                return result;
            }

            string commandArg = args[0].ToLowerInvariant();
            switch (commandArg)
            {
                case "start":
                    result.Command = HotReloadCommand.Start;
                    break;
                case "stop":
                    result.Command = HotReloadCommand.Stop;
                    break;
                case "status":
                    result.Command = HotReloadCommand.Status;
                    break;
                case "help":
                case "--help":
                case "-h":
                case "/?":
                    result.Command = HotReloadCommand.Help;
                    return result;
                default:
                    result.Errors.Add("Unknown command: " + commandArg);
                    result.Command = HotReloadCommand.Help;
                    return result;
            }

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.Equals("--project", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.ProjectPath = args[++i];
                    else
                        result.Errors.Add("Missing value for --project.");
                }
                else if (arg.Equals("--debounce", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        int debounce;
                        if (int.TryParse(args[++i], out debounce))
                            result.DebounceMilliseconds = debounce;
                        else
                            result.Errors.Add("Missing or invalid value for --debounce.");
                    }
                    else
                        result.Errors.Add("Missing or invalid value for --debounce.");
                }
                else if (arg.Equals("--drain-timeout", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        int timeout;
                        if (int.TryParse(args[++i], out timeout))
                            result.DrainTimeoutMilliseconds = timeout;
                        else
                            result.Errors.Add("Missing or invalid value for --drain-timeout.");
                    }
                    else
                        result.Errors.Add("Missing or invalid value for --drain-timeout.");
                }
                else if (arg.Equals("--pipe-name", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.PipeName = args[++i];
                    else
                        result.Errors.Add("Missing value for --pipe-name.");
                }
                else if (arg.Equals("--watch", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-w", StringComparison.OrdinalIgnoreCase))
                {
                    result.Watch = true;
                }
                else if (arg.Equals("--mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        string modeValue = args[++i];
                        if (modeValue.Equals("process", StringComparison.OrdinalIgnoreCase))
                            result.Mode = ReloadMode.Process;
                        else if (modeValue.Equals("build-recycle", StringComparison.OrdinalIgnoreCase))
                            result.Mode = ReloadMode.BuildRecycle;
                        else
                            result.Errors.Add("Invalid value for --mode. Expected 'process' or 'build-recycle'.");
                    }
                    else
                        result.Errors.Add("Missing value for --mode.");
                }
                else if (arg.Equals("--extensions", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.ExtensionsCsv = args[++i];
                    else
                        result.Errors.Add("Missing value for --extensions.");
                }
                else if (arg.Equals("--deploy-to", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.DeployToDirectory = args[++i];
                    else
                        result.Errors.Add("Missing value for --deploy-to.");
                }
                else if (arg.Equals("--recycle-target", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.RecycleTargetPath = args[++i];
                    else
                        result.Errors.Add("Missing value for --recycle-target.");
                }
                else if (arg.Equals("--web-watch", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.WebWatchDirectory = args[++i];
                    else
                        result.Errors.Add("Missing value for --web-watch.");
                }
                else if (arg.Equals("--web-extensions", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.WebExtensionsCsv = args[++i];
                    else
                        result.Errors.Add("Missing value for --web-extensions.");
                }
                else if (arg.Equals("--solution-project", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-sp", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.SolutionProjectName = args[++i];
                    else
                        result.Errors.Add("Missing value for --solution-project.");
                }
                else if (arg.Equals("--no-pdb", StringComparison.OrdinalIgnoreCase))
                {
                    result.DeployPdb = false;
                }
                else if (arg.Equals("--site-url", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        result.SiteUrl = args[++i];
                    else
                        result.Errors.Add("Missing value for --site-url.");
                }
                else if (!string.IsNullOrEmpty(arg) && arg[0] != '-')
                {
                    // Positional: treat as project path if not already set
                    if (string.IsNullOrEmpty(result.ProjectPath))
                        result.ProjectPath = arg;
                    else
                        result.UnrecognizedArgs.Add(arg);
                }
                else
                {
                    result.UnrecognizedArgs.Add(arg);
                }
            }

            if (result.Command == HotReloadCommand.Start && string.IsNullOrEmpty(result.ProjectPath))
                result.Errors.Add("--project is required for the 'start' command.");

            if (result.Command == HotReloadCommand.Start && result.Mode == ReloadMode.BuildRecycle)
            {
                if (string.IsNullOrWhiteSpace(result.DeployToDirectory))
                    result.Errors.Add("--deploy-to is required for the 'build-recycle' mode.");
                if (string.IsNullOrWhiteSpace(result.RecycleTargetPath))
                    result.Errors.Add("--recycle-target is required for the 'build-recycle' mode.");
            }

            return result;
        }

        /// <summary>
        /// Builds the argument list for the child <c>start</c> process spawned by
        /// <c>watch</c>. Forwards project path, debounce, drain timeout, and pipe
        /// name, plus — only when set/non-default — <c>--mode</c>,
        /// <c>--extensions</c>, <c>--deploy-to</c>, <c>--recycle-target</c>,
        /// <c>--solution-project</c>, and <c>--no-pdb</c>, so the child behaves
        /// like a direct <c>start</c> invocation.
        /// </summary>
        /// <returns>Arguments including the leading <c>start</c> command.</returns>
        public string[] BuildWatchStartArgs()
        {
            var startArgs = new List<string> { "start" };
            if (!string.IsNullOrEmpty(ProjectPath))
            {
                startArgs.Add("-p");
                startArgs.Add(ProjectPath);
            }
            if (DebounceMilliseconds.HasValue)
            {
                startArgs.Add("--debounce");
                startArgs.Add(DebounceMilliseconds.Value.ToString());
            }
            if (DrainTimeoutMilliseconds.HasValue)
            {
                startArgs.Add("--drain-timeout");
                startArgs.Add(DrainTimeoutMilliseconds.Value.ToString());
            }
            if (!string.IsNullOrEmpty(PipeName))
            {
                startArgs.Add("--pipe-name");
                startArgs.Add(PipeName);
            }
            if (Mode == ReloadMode.BuildRecycle)
            {
                startArgs.Add("--mode");
                startArgs.Add("build-recycle");
            }
            if (!string.IsNullOrEmpty(ExtensionsCsv))
            {
                startArgs.Add("--extensions");
                startArgs.Add(ExtensionsCsv);
            }
            if (!string.IsNullOrEmpty(DeployToDirectory))
            {
                startArgs.Add("--deploy-to");
                startArgs.Add(DeployToDirectory);
            }
            if (!string.IsNullOrEmpty(RecycleTargetPath))
            {
                startArgs.Add("--recycle-target");
                startArgs.Add(RecycleTargetPath);
            }
            if (!string.IsNullOrEmpty(SolutionProjectName))
            {
                startArgs.Add("--solution-project");
                startArgs.Add(SolutionProjectName);
            }
            if (!DeployPdb)
            {
                startArgs.Add("--no-pdb");
            }
            if (!string.IsNullOrEmpty(WebWatchDirectory))
            {
                startArgs.Add("--web-watch");
                startArgs.Add(WebWatchDirectory);
            }
            if (!string.IsNullOrEmpty(WebExtensionsCsv))
            {
                startArgs.Add("--web-extensions");
                startArgs.Add(WebExtensionsCsv);
            }
            if (!string.IsNullOrEmpty(SiteUrl))
            {
                startArgs.Add("--site-url");
                startArgs.Add(SiteUrl);
            }
            return startArgs.ToArray();
        }
    }
}
