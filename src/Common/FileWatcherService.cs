using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HotReloadTool.Common
{
    /// <summary>
    /// Wraps <see cref="FileSystemWatcher"/> with configurable extension
    /// filtering, glob-style exclusion patterns, and debounce coalescing.
    /// Raises a single <see cref="FileChanged"/> event per quiet period.
    /// </summary>
    /// <remarks>
    /// Thread safety: <see cref="FileChanged"/> is raised on a thread-pool thread
    /// via the debounce timer. Subscribers MUST be thread-safe or marshal to
    /// their own synchronization context.
    /// </remarks>
    public class FileWatcherService : IDisposable, IFileWatcherService
    {
        private readonly ReloadConfiguration _config;
        private FileSystemWatcher _watcher;
        private System.Timers.Timer _debounceTimer;
        private readonly object _sync = new object();
        private bool _disposed;
        private readonly List<string> _pendingPaths = new List<string>();

        /// <summary>
        /// Raised once per debounce quiet period with the file paths that changed.
        /// </summary>
        public event EventHandler<FileChangedEventArgs> FileChanged;

        /// <summary>
        /// Raised when the underlying <see cref="FileSystemWatcher"/> encounters
        /// an error (e.g., buffer overflow, directory deleted).
        /// </summary>
        public event EventHandler<ErrorEventArgs> Error;

        /// <summary>
        /// Creates a new <see cref="FileWatcherService"/> with the given configuration.
        /// Does not begin watching until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="config">Configuration with filters and debounce settings.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is <c>null</c>.
        /// </exception>
        public FileWatcherService(ReloadConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException("config");
            _config = config;
        }

        /// <summary>
        /// Starts monitoring the configured directory. If already started, this
        /// call is a no-op.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="ReloadConfiguration.GetEffectiveWatchDirectory"/>
        /// returns <c>null</c>.
        /// </exception>
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException("FileWatcherService");

            if (_watcher != null)
                return;

            string directory = _config.GetEffectiveWatchDirectory();
            if (directory == null)
                throw new InvalidOperationException("Watch directory could not be determined from configuration.");

            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true
            };

            _watcher.Changed += OnWatcherEvent;
            _watcher.Created += OnWatcherEvent;
            _watcher.Renamed += OnRenamedEvent;
            _watcher.Error += OnWatcherError;

            _debounceTimer = new System.Timers.Timer(_config.DebounceMilliseconds);
            _debounceTimer.Elapsed += OnDebounceElapsed;
            _debounceTimer.AutoReset = false;
        }

        /// <summary>
        /// Stops monitoring and releases the underlying <see cref="FileSystemWatcher"/>.
        /// </summary>
        public void Stop()
        {
            if (_disposed)
                return;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnWatcherEvent;
                _watcher.Created -= OnWatcherEvent;
                _watcher.Renamed -= OnRenamedEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
        }

        /// <summary>
        /// Tests whether the given file path matches the extension filter and
        /// does NOT match any exclusion pattern. Public for unit testing.
        /// Handles compound extensions such as <c>.aspx.vb</c> by matching
        /// against the full file name rather than only the last extension
        /// segment (which <see cref="Path.GetExtension"/> would return).
        /// </summary>
        /// <param name="filePath">Absolute or relative file path.</param>
        /// <returns><c>true</c> if the file should trigger a reload.</returns>
        public bool ShouldInclude(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            string fileName = Path.GetFileName(filePath);

            bool matchesExtension = _config.Extensions != null
                && _config.Extensions.Any(e =>
                {
                    string pattern = e.TrimStart('*');
                    return fileName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
                });

            if (!matchesExtension)
                return false;

            if (_config.ExclusionPatterns != null)
            {
                foreach (string exclusion in _config.ExclusionPatterns)
                {
                    if (MatchesGlob(filePath, exclusion))
                        return false;
                }
            }

            return true;
        }

        private void OnWatcherEvent(object sender, FileSystemEventArgs e)
        {
            HandleChange(e.FullPath);
        }

        private void OnRenamedEvent(object sender, RenamedEventArgs e)
        {
            HandleChange(e.FullPath);
        }

        private void HandleChange(string path)
        {
            if (!ShouldInclude(path))
                return;

            lock (_sync)
            {
                _pendingPaths.Add(path);
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        private void OnDebounceElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            List<string> paths;
            lock (_sync)
            {
                paths = new List<string>(_pendingPaths);
                _pendingPaths.Clear();
            }

            if (paths.Count > 0)
                FileChanged.Raise(this, new FileChangedEventArgs(paths));
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Error.Raise(this, e);
        }

        /// <summary>
        /// Matches a path against a glob pattern. Supports <c>**</c> for any
        /// directory depth and <c>*</c> for single-segment wildcards.
        /// </summary>
        public static bool MatchesGlob(string path, string pattern)
        {
            // Normalize to forward slashes for matching
            string normalizedPath = path.Replace('\\', '/');

            // Build regex: handle ** first (any depth), then * (single segment)
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace(@"\*\*", "DOUBLESTAR")
                .Replace(@"\*", @"[^/]*")
                .Replace("DOUBLESTAR", ".*") + "$";

            // Normalize pattern separators too
            regexPattern = regexPattern.Replace(@"\/", "/");

            return Regex.IsMatch(normalizedPath, regexPattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Releases resources. After disposal, the service cannot be restarted.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
        }
    }

    /// <summary>
    /// Event arguments for the <see cref="FileWatcherService.FileChanged"/> event.
    /// </summary>
    public class FileChangedEventArgs : EventArgs
    {
        private readonly IReadOnlyList<string> _paths;
        private readonly DateTime _timestamp;

        /// <summary>File paths that changed during the debounce window.</summary>
        public IReadOnlyList<string> Paths { get { return _paths; } }

        /// <summary>Time the debounce window closed and the event was raised.</summary>
        public DateTime Timestamp { get { return _timestamp; } }

        /// <summary>
        /// Creates a new <see cref="FileChangedEventArgs"/>.
        /// </summary>
        /// <param name="paths">The changed file paths.</param>
        public FileChangedEventArgs(IReadOnlyList<string> paths)
        {
            _paths = paths;
            _timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Extension to raise events with null-check.
    /// </summary>
    internal static class EventHandlerExtensions
    {
        public static void Raise<TArgs>(this EventHandler<TArgs> handler, object sender, TArgs args)
            where TArgs : EventArgs
        {
            if (handler != null)
                handler(sender, args);
        }

        public static void Raise(this EventHandler<ErrorEventArgs> handler, object sender, ErrorEventArgs args)
        {
            if (handler != null)
                handler(sender, args);
        }
    }
}
