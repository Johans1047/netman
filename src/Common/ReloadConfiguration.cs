using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HotReloadTool.Common
{
    /// <summary>
    /// Reload pipeline modes supported by the Hot Reload tool.
    /// </summary>
    public enum ReloadMode
    {
        /// <summary>
        /// Default pipeline: build → shadow-copy → drain → state transfer →
        /// relaunch the worker .exe.
        /// </summary>
        Process = 0,

        /// <summary>
        /// Legacy ASP.NET pipeline: build a library project → copy the output
        /// DLL into the web site's Bin folder → touch a file (e.g. web.config)
        /// so ASP.NET recycles the AppDomain. No worker process is spawned.
        /// </summary>
        BuildRecycle
    }

    /// <summary>
    /// Strongly-typed configuration for the Hot Reload tool. Controls file
    /// watching filters, debounce intervals, drain timeouts, Named Pipes
    /// endpoint naming, and the reload mode (process or build-recycle).
    /// Loaded from CLI arguments and/or a config file.
    /// </summary>
    public class ReloadConfiguration
    {
        /// <summary>
        /// Reload pipeline mode. Default: <see cref="ReloadMode.Process"/>.
        /// </summary>
        public ReloadMode Mode { get; set; }

        private string _extensionsCsv;
        private string _webExtensionsCsv;

        /// <summary>
        /// Raw comma-separated watch-extension filter (e.g. <c>*.vb,*.config</c>).
        /// Assigning a value replaces <see cref="Extensions"/> entirely;
        /// assigning <c>null</c> leaves the current list (the process-mode
        /// defaults) untouched. Empty or whitespace entries are rejected by
        /// <see cref="Validate"/>.
        /// </summary>
        public string ExtensionsCsv
        {
            get { return _extensionsCsv; }
            set
            {
                _extensionsCsv = value;
                if (value == null)
                    return;

                Extensions = value
                    .Split(',')
                    .Select(entry => entry.Trim())
                    .ToList();
            }
        }

        /// <summary>
        /// Directory that receives the built library output (build-recycle
        /// mode). Required when <see cref="Mode"/> is
        /// <see cref="ReloadMode.BuildRecycle"/>.
        /// </summary>
        public string DeployToDirectory { get; set; }

        /// <summary>
        /// File to touch so ASP.NET recycles the AppDomain (build-recycle
        /// mode), e.g. the web site's web.config. Required when
        /// <see cref="Mode"/> is <see cref="ReloadMode.BuildRecycle"/>.
        /// </summary>
        public string RecycleTargetPath { get; set; }

        /// <summary>
        /// Optional second directory to watch for web-file changes
        /// (<c>*.aspx</c>, <c>*.aspx.vb</c>) that do not require a library
        /// rebuild — only an AppDomain recycle. When set, a change here
        /// skips the build/deploy phases and goes straight to recycling.
        /// </summary>
        public string WebWatchDirectory { get; set; }

        /// <summary>
        /// Raw comma-separated web-watch-extension filter (e.g.
        /// <c>*.aspx,*.aspx.vb</c>). Assigning a value replaces
        /// <see cref="WebExtensions"/> entirely; assigning <c>null</c>
        /// leaves the current list untouched.
        /// </summary>
        public string WebExtensionsCsv
        {
            get { return _webExtensionsCsv; }
            set
            {
                _webExtensionsCsv = value;
                if (value == null)
                    return;

                WebExtensions = value
                    .Split(',')
                    .Select(entry => entry.Trim())
                    .ToList();
            }
        }

        /// <summary>
        /// File extensions under <see cref="WebWatchDirectory"/> that trigger
        /// a recycle when changed. Default: <c>["*.aspx","*.aspx.vb"]</c>.
        /// </summary>
        public List<string> WebExtensions { get; set; }

        /// <summary>
        /// Base URL of the running site (e.g.
        /// <c>http://localhost:12345/SIPAF/</c>), used to print the direct
        /// URL of the page a web-watch change just recycled. Optional — when
        /// unset, no URL is printed after a web-file recycle. Only meaningful
        /// together with <see cref="WebWatchDirectory"/>: the printed URL is
        /// this base plus the changed file's path relative to
        /// <see cref="WebWatchDirectory"/>, with a trailing <c>.vb</c> (a
        /// code-behind change) stripped so it points at the <c>.aspx</c> page.
        /// </summary>
        public string SiteBaseUrl { get; set; }

        /// <summary>
        /// Absolute or relative path to the worker project file (.csproj) to
        /// monitor and compile. Required. When this points to a .sln,
        /// <see cref="SolutionProjectName"/> must also be set.
        /// </summary>
        public string ProjectPath { get; set; }

        /// <summary>
        /// Project name within a solution whose output assembly to deploy.
        /// Required when <see cref="ProjectPath"/> is a .sln. Ignored for
        /// single-project builds.
        /// </summary>
        public string SolutionProjectName { get; set; }

        /// <summary>
        /// When true (default), deploy the .pdb alongside the DLL in
        /// build-recycle mode.
        /// </summary>
        public bool DeployPdb { get; set; }

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
            Mode = ReloadMode.Process;
            Extensions = new List<string> { "*.cs", "*.config" };
            ExclusionPatterns = new List<string> { "**/obj/**", "**/bin/**", "**/*.tmp" };
            WebExtensions = new List<string> { "*.aspx", "*.aspx.vb" };
            DebounceMilliseconds = 500;
            DrainTimeoutMilliseconds = 3000;
            StateTransferTimeoutMilliseconds = 5000;
            PipeName = Contracts.ReloadMessages.DefaultPipeName;
            RetainedShadowCopies = 3;
            DeployPdb = true;
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

            if (Mode == ReloadMode.BuildRecycle)
            {
                if (string.IsNullOrWhiteSpace(DeployToDirectory))
                    throw new ArgumentException("DeployToDirectory is required in build-recycle mode.", "DeployToDirectory");

                if (string.IsNullOrWhiteSpace(RecycleTargetPath))
                    throw new ArgumentException("RecycleTargetPath is required in build-recycle mode.", "RecycleTargetPath");
            }

            if (!string.IsNullOrWhiteSpace(WebWatchDirectory))
            {
                if (WebExtensions == null || WebExtensions.Count == 0)
                    throw new ArgumentException("At least one web extension must be specified when WebWatchDirectory is set.", "WebExtensions");

                if (WebExtensions.Any(e => string.IsNullOrWhiteSpace(e)))
                    throw new ArgumentException("Web extension entries must not be null or empty.", "WebExtensions");
            }

            if (!string.IsNullOrWhiteSpace(ProjectPath)
                && ProjectPath.TrimEnd().EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(SolutionProjectName))
                    throw new ArgumentException("SolutionProjectName is required when ProjectPath is a solution (.sln).", "SolutionProjectName");
            }
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
                    return Path.GetDirectoryName(Path.GetFullPath(ProjectPath));
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
