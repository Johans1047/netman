using System;
using System.Collections.Generic;

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

        /// <summary>Path to the worker project (.csproj). Required for <c>start</c>.</summary>
        public string ProjectPath { get; set; }

        /// <summary>Override for debounce interval in milliseconds.</summary>
        public int? DebounceMilliseconds { get; set; }

        /// <summary>Override for drain timeout in milliseconds.</summary>
        public int? DrainTimeoutMilliseconds { get; set; }

        /// <summary>Override for Named Pipes endpoint name.</summary>
        public string PipeName { get; set; }

        /// <summary>Watch source files for changes and auto-rebuild.</summary>
        public bool Watch { get; set; }

        /// <summary>Unparsed arguments that did not match known options.</summary>
        public List<string> UnrecognizedArgs { get; set; }

        /// <summary>Parsing errors encountered.</summary>
        public List<string> Errors { get; set; }

        public CommandLineArgs()
        {
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

            return result;
        }
    }
}
