using System;
using System.IO;

namespace HotReloadTool.Common
{
    /// <summary>
    /// Abstraction for file watching. Implemented by
    /// <see cref="FileWatcherService"/>; allows test doubles to be injected.
    /// </summary>
    public interface IFileWatcherService : IDisposable
    {
        /// <summary>Starts monitoring the configured directory.</summary>
        void Start();

        /// <summary>Stops monitoring.</summary>
        void Stop();

        /// <summary>
        /// Raised once per debounce quiet period with the file paths that changed.
        /// </summary>
        event EventHandler<FileChangedEventArgs> FileChanged;

        /// <summary>
        /// Raised when the underlying watcher encounters an error.
        /// </summary>
        event EventHandler<ErrorEventArgs> Error;
    }
}
