using System;
using System.IO;
using System.Threading;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Result of a library deploy attempt. Returned by
    /// <see cref="LibraryDeployer.Deploy"/> to gate the recycle step of the
    /// build-recycle cycle. Expected I/O failures are reported here instead
    /// of being thrown.
    /// </summary>
    public class DeployResult
    {
        /// <summary><c>true</c> when the assembly was copied to the deploy directory.</summary>
        public bool Success { get; set; }

        /// <summary>Absolute path of the destination file; <c>null</c> on failure.</summary>
        public string DestinationPath { get; set; }

        /// <summary>Error message when the deploy failed.</summary>
        public string Error { get; set; }

        /// <summary>Number of copy attempts performed (0 when guarded before any attempt).</summary>
        public int Attempts { get; set; }

        /// <summary>Creates a failed result with the given error message and attempt count.</summary>
        /// <param name="error">Error message describing the failure.</param>
        /// <param name="attempts">Number of copy attempts performed.</param>
        public static DeployResult Failed(string error, int attempts)
        {
            return new DeployResult { Success = false, Error = error, Attempts = attempts };
        }
    }

    /// <summary>
    /// Copies the built library assembly into the web site's Bin folder
    /// (build-recycle mode). Creates the deploy directory when missing and
    /// retries transient I/O failures (e.g. a locked destination file)
    /// before returning a structured <see cref="DeployResult"/>.
    /// </summary>
    /// <remarks>
    /// Only the primary output assembly (the single
    /// <c>sourceAssemblyPath</c>) is deployed; satellite and dependency
    /// DLLs are out of scope (documented non-goal).
    /// Expected I/O failures (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) are reported via
    /// <see cref="DeployResult"/> and never thrown. Argument errors
    /// (null/empty) still throw <see cref="ArgumentException"/>.
    /// The PDB is deployed alongside the DLL on a best-effort basis when
    /// <paramref name="deployPdb"/> is true — PDB copy failures are silently
    /// ignored and never fail the overall deploy.
    /// </remarks>
    public class LibraryDeployer
    {
        private readonly int _maxAttempts;
        private readonly int _retryDelayMilliseconds;

        /// <summary>
        /// Creates a new <see cref="LibraryDeployer"/>.
        /// </summary>
        /// <param name="maxAttempts">
        /// Total number of copy attempts before giving up. Default: 3.
        /// </param>
        /// <param name="retryDelayMilliseconds">
        /// Delay between retries. Injectable/short so tests stay fast.
        /// Default: 200.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="maxAttempts"/> is less than 1 or
        /// <paramref name="retryDelayMilliseconds"/> is negative.
        /// </exception>
        public LibraryDeployer(int maxAttempts = 3, int retryDelayMilliseconds = 200)
        {
            if (maxAttempts < 1)
                throw new ArgumentException("maxAttempts must be >= 1.", "maxAttempts");

            if (retryDelayMilliseconds < 0)
                throw new ArgumentException("retryDelayMilliseconds must be >= 0.", "retryDelayMilliseconds");

            _maxAttempts = maxAttempts;
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        /// <summary>
        /// Copies <paramref name="sourceAssemblyPath"/> into
        /// <paramref name="deployDirectory"/> (creating it when missing),
        /// overwriting any existing destination file. Retries on
        /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>
        /// (locked destination) with the configured delay between attempts.
        /// When <paramref name="deployPdb"/> is true, a .pdb with the same
        /// name is copied alongside the DLL on a best-effort basis.
        /// </summary>
        /// <param name="sourceAssemblyPath">Absolute path of the built assembly.</param>
        /// <param name="deployDirectory">Directory that receives the assembly (e.g. the site's Bin).</param>
        /// <param name="deployPdb">When true (default), deploy the .pdb alongside the DLL.</param>
        /// <returns>
        /// A <see cref="DeployResult"/> indicating success or failure —
        /// expected I/O failures are reported, not thrown.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="sourceAssemblyPath"/> or
        /// <paramref name="deployDirectory"/> is null or empty.
        /// </exception>
        public DeployResult Deploy(string sourceAssemblyPath, string deployDirectory, bool deployPdb = true)
        {
            if (string.IsNullOrWhiteSpace(sourceAssemblyPath))
                throw new ArgumentException("sourceAssemblyPath is required.", "sourceAssemblyPath");

            if (string.IsNullOrWhiteSpace(deployDirectory))
                throw new ArgumentException("deployDirectory is required.", "deployDirectory");

            if (!File.Exists(sourceAssemblyPath))
                return DeployResult.Failed("Source assembly not found: " + sourceAssemblyPath, 0);

            string destinationPath = Path.Combine(deployDirectory, Path.GetFileName(sourceAssemblyPath));
            string lastError = null;

            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(deployDirectory);
                    File.Copy(sourceAssemblyPath, destinationPath, overwrite: true);

                    DeployPdbBestEffort(sourceAssemblyPath, deployDirectory, deployPdb);

                    return new DeployResult
                    {
                        Success = true,
                        DestinationPath = destinationPath,
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

            return DeployResult.Failed(
                "Deploy failed after " + _maxAttempts + " attempt(s): " + lastError,
                _maxAttempts);
        }

        /// <summary>
        /// Copies the .pdb next to <paramref name="sourceAssemblyPath"/> into
        /// <paramref name="deployDirectory"/> when it exists and
        /// <paramref name="deployPdb"/> is true. Best-effort: silently skips
        /// when the PDB is missing or the copy fails. The DLL deploy result
        /// is authoritative.
        /// </summary>
        private static void DeployPdbBestEffort(string sourceAssemblyPath, string deployDirectory, bool deployPdb)
        {
            if (!deployPdb)
                return;

            try
            {
                string pdbSource = Path.ChangeExtension(sourceAssemblyPath, ".pdb");
                if (File.Exists(pdbSource))
                {
                    string pdbDest = Path.Combine(deployDirectory, Path.GetFileName(pdbSource));
                    File.Copy(pdbSource, pdbDest, overwrite: true);
                }
            }
            catch
            {
                // Best-effort: PDB copy failures must not fail the overall deploy.
            }
        }
    }
}
