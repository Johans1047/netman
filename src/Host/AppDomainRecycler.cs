using System;
using System.IO;
using System.Threading;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Result of an AppDomain recycle attempt. Returned by
    /// <see cref="AppDomainRecycler.Recycle"/> to gate the settle-back-to-Idle
    /// step of the build-recycle cycle. Expected I/O failures are reported
    /// here instead of being thrown.
    /// </summary>
    public class RecycleResult
    {
        /// <summary><c>true</c> when the recycle target's last-write timestamp was updated.</summary>
        public bool Success { get; set; }

        /// <summary>Absolute path of the touched recycle target; <c>null</c> on failure.</summary>
        public string TargetPath { get; set; }

        /// <summary>Error message when the recycle failed.</summary>
        public string Error { get; set; }

        /// <summary>Number of touch attempts performed (0 when guarded before any attempt).</summary>
        public int Attempts { get; set; }

        /// <summary>Creates a failed result with the given error message and attempt count.</summary>
        /// <param name="error">Error message describing the failure.</param>
        /// <param name="attempts">Number of touch attempts performed.</param>
        public static RecycleResult Failed(string error, int attempts)
        {
            return new RecycleResult { Success = false, Error = error, Attempts = attempts };
        }
    }

    /// <summary>
    /// Triggers an ASP.NET AppDomain recycle by touching (updating only the
    /// last-write timestamp of) the configured recycle target file, typically
    /// the web site's web.config (build-recycle mode).
    /// WHY: ASP.NET's FileSystemWatcher sees the LastWrite change on
    /// web.config and restarts the AppDomain, loading the newly deployed Bin
    /// DLL; the file's content is never rewritten — it stays byte-identical.
    /// Retries transient I/O failures (e.g. a momentarily locked target)
    /// before returning a structured <see cref="RecycleResult"/>.
    /// </summary>
    /// <remarks>
    /// Expected I/O failures (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) are reported via
    /// <see cref="RecycleResult"/> and never thrown. Argument errors
    /// (null/empty) still throw <see cref="ArgumentException"/>.
    /// </remarks>
    public class AppDomainRecycler
    {
        private readonly int _maxAttempts;
        private readonly int _retryDelayMilliseconds;

        /// <summary>
        /// Creates a new <see cref="AppDomainRecycler"/>.
        /// </summary>
        /// <param name="maxAttempts">
        /// Total number of touch attempts before giving up. Default: 3.
        /// </param>
        /// <param name="retryDelayMilliseconds">
        /// Delay between retries. Injectable/short so tests stay fast.
        /// Default: 200.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="maxAttempts"/> is less than 1 or
        /// <paramref name="retryDelayMilliseconds"/> is negative.
        /// </exception>
        public AppDomainRecycler(int maxAttempts = 3, int retryDelayMilliseconds = 200)
        {
            if (maxAttempts < 1)
                throw new ArgumentException("maxAttempts must be >= 1.", "maxAttempts");

            if (retryDelayMilliseconds < 0)
                throw new ArgumentException("retryDelayMilliseconds must be >= 0.", "retryDelayMilliseconds");

            _maxAttempts = maxAttempts;
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        /// <summary>
        /// Touches <paramref name="recycleTargetPath"/> by setting its
        /// last-write timestamp to the current UTC time (content is left
        /// byte-identical), so ASP.NET's FileSystemWatcher observes the change
        /// and recycles the AppDomain. Retries on
        /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>
        /// (locked target) with the configured delay between attempts.
        /// </summary>
        /// <param name="recycleTargetPath">Absolute path of the file to touch (e.g. web.config).</param>
        /// <returns>
        /// A <see cref="RecycleResult"/> indicating success or failure —
        /// expected I/O failures are reported, not thrown.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="recycleTargetPath"/> is null or empty.
        /// </exception>
        public RecycleResult Recycle(string recycleTargetPath)
        {
            if (string.IsNullOrWhiteSpace(recycleTargetPath))
                throw new ArgumentException("recycleTargetPath is required.", "recycleTargetPath");

            if (!File.Exists(recycleTargetPath))
                return RecycleResult.Failed("Recycle target not found: " + recycleTargetPath, 0);

            string lastError = null;

            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    // Touch only: updates LastWrite, never file content.
                    File.SetLastWriteTimeUtc(recycleTargetPath, DateTime.UtcNow);

                    return new RecycleResult
                    {
                        Success = true,
                        TargetPath = recycleTargetPath,
                        Attempts = attempt
                    };
                }
                catch (IOException ex)
                {
                    lastError = ex.Message;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex.Message;
                }

                if (attempt < _maxAttempts && _retryDelayMilliseconds > 0)
                    Thread.Sleep(_retryDelayMilliseconds);
            }

            return RecycleResult.Failed(
                "Recycle failed after " + _maxAttempts + " attempt(s): " + lastError,
                _maxAttempts);
        }
    }
}
