using System;
using System.Collections.Generic;
using System.Linq;

namespace HotReloadTool.Common
{
    /// <summary>
    /// Strongly-typed configuration for the Hot Reload tool. Controls file
    /// watching filters, debounce intervals, drain timeouts, and Named Pipes
    /// endpoint naming. Loaded from CLI arguments and/or a config file.
    /// </summary>
    public class ReloadConfiguration
    {
        /// <summary>
        /// Absolute or relative path to the worker project file (.csproj) to
        /// monitor and compile. Required.
        /// </summary>
        public string ProjectPath { get; set; }

        /// <summary>
        /// Absolute or relative path to the source directory to watch.
        /// Defaults to the directory containing <see cref="ProjectPath"/>.
        /// </summary>
        public string WatchDirectory { get; set; }

        /// <summary>
        /// File extensions that trigger a reload when changed.
        /// Default: <c>["*.cs","*.config"]</c>.
        /// </summary>
        public List<string> Extensions { get; set; }

        /// <summary>
        /// Glob-style exclusion patterns. Files matching any pattern are ignored.
        /// Default: <c>["**/obj/**","**/bin/**","**/*.tmp"]</c>.
        /// </summary>
        public List<string> ExclusionPatterns { get; set; }

        /// <summary>
        /// Debounce interval in milliseconds. Rapid file changes within this
        /// window are coalesced into a single reload cycle. Default: 500.
        /// </summary>
        public int DebounceMilliseconds { get; set; }

        /// <summary>
        /// Maximum time in milliseconds to wait for connection draining before
        /// force-terminating the old worker. Default: 3000.
        /// </summary>
        public int DrainTimeoutMilliseconds { get; set; }

        /// <summary>
        /// Maximum time in milliseconds to wait for Named Pipes state transfer
        /// before falling back to stateless restart. Default: 5000.
        /// </summary>
        public int StateTransferTimeoutMilliseconds { get; set; }

        /// <summary>
        /// Named Pipes endpoint name for IPC. Default: "hotreload-state".
        /// </summary>
        public string PipeName { get; set; }

        /// <summary>
        /// Number of shadow copies to retain. Older copies are deleted on each
        /// successful launch. Default: 3.
        /// </summary>
        public int RetainedShadowCopies { get; set; }

        /// <summary>
        /// Creates a <see cref="ReloadConfiguration"/> with default values.
        /// <see cref="ProjectPath"/> and <see cref="WatchDirectory"/> are left
        /// unset and MUST be assigned before use.
        /// </summary>
        public ReloadConfiguration()
        {
            Extensions = new List<string> { "*.cs", "*.config" };
            ExclusionPatterns = new List<string> { "**/obj/**", "**/bin/**", "**/*.tmp" };
            DebounceMilliseconds = 500;
            DrainTimeoutMilliseconds = 3000;
            StateTransferTimeoutMilliseconds = 5000;
            PipeName = Contracts.ReloadMessages.DefaultPipeName;
            RetainedShadowCopies = 3;
        }

        /// <summary>
        /// Validates that all required fields are populated and values are within
        /// acceptable ranges. Throws <see cref="ArgumentException"/> on failure.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <see cref="ProjectPath"/> is null/empty, paths are invalid,
        /// or numeric values are out of range.
        /// </exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ProjectPath))
                throw new ArgumentException("ProjectPath is required.", "ProjectPath");

            if (DebounceMilliseconds < 0)
                throw new ArgumentException("DebounceMilliseconds must be >= 0.", "DebounceMilliseconds");

            if (DrainTimeoutMilliseconds < 0)
                throw new ArgumentException("DrainTimeoutMilliseconds must be >= 0.", "DrainTimeoutMilliseconds");

            if (StateTransferTimeoutMilliseconds < 0)
                throw new ArgumentException("StateTransferTimeoutMilliseconds must be >= 0.", "StateTransferTimeoutMilliseconds");

            if (Extensions == null || Extensions.Count == 0)
                throw new ArgumentException("At least one extension must be specified.", "Extensions");

            if (Extensions.Any(e => string.IsNullOrWhiteSpace(e)))
                throw new ArgumentException("Extension entries must not be null or empty.", "Extensions");
        }

        /// <summary>
        /// Returns the effective watch directory — explicit value if set,
        /// otherwise derived from the project file's parent directory.
        /// </summary>
        public string GetEffectiveWatchDirectory()
        {
            if (!string.IsNullOrWhiteSpace(WatchDirectory))
                return WatchDirectory;

            if (!string.IsNullOrWhiteSpace(ProjectPath))
            {
                try
                {
                    return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ProjectPath));
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }
}
