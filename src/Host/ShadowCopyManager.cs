using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Manages shadow-copy directories for the Hot Reload tool. Copies build
    /// output to a timestamped temp directory so the new worker can launch
    /// without file locks on the original build output. Retains the last
    /// N shadow copies and cleans up older ones on each successful launch.
    /// </summary>
    /// <remarks>
    /// Shadow directory layout: <c>%TEMP%\hotreload\{ProjectGuid}\{Timestamp}\</c>
    /// where Timestamp is <c>yyyyMMddHHmmss</c> UTC.
    /// </remarks>
    public class ShadowCopyManager : IShadowCopyManager
    {
        private readonly string _shadowRoot;
        private readonly int _retainCount;

        /// <summary>
        /// Creates a new <see cref="ShadowCopyManager"/>.
        /// </summary>
        /// <param name="projectGuid">
        /// Unique identifier for the project (used to namespace shadow copies).
        /// </param>
        /// <param name="retainCount">
        /// Number of shadow copies to retain. Older copies are deleted on each
        /// successful launch. Default: 3.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="projectGuid"/> is null or empty.
        /// </exception>
        public ShadowCopyManager(string projectGuid, int retainCount = 3)
        {
            if (string.IsNullOrWhiteSpace(projectGuid))
                throw new ArgumentException("projectGuid is required.", "projectGuid");

            _shadowRoot = Path.Combine(Path.GetTempPath(), "hotreload", projectGuid);
            _retainCount = retainCount;
        }

        /// <summary>
        /// Copies all files from the build output directory into a new
        /// timestamped shadow directory.
        /// </summary>
        /// <param name="buildOutputPath">
        /// Absolute path to the build output directory (e.g., <c>bin\Debug\</c>).
        /// </param>
        /// <returns>Absolute path to the new shadow copy directory.</returns>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when <paramref name="buildOutputPath"/> does not exist.
        /// </exception>
        public string CopyToTemp(string buildOutputPath)
        {
            if (!Directory.Exists(buildOutputPath))
                throw new DirectoryNotFoundException("Build output not found: " + buildOutputPath);

            string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string shadowDir = Path.Combine(_shadowRoot, timestamp);
            Directory.CreateDirectory(shadowDir);

            foreach (string file in Directory.GetFiles(buildOutputPath))
            {
                string dest = Path.Combine(shadowDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            return shadowDir;
        }

        /// <summary>
        /// Removes shadow copy directories older than the most recent
        /// <see cref="_retainCount"/>, keeping the newest copies intact.
        /// Called after a successful worker launch.
        /// </summary>
        public void CleanupOldCopies()
        {
            if (!Directory.Exists(_shadowRoot))
                return;

            var dirs = Directory.GetDirectories(_shadowRoot)
                .OrderByDescending(d => d)
                .Skip(_retainCount)
                .ToList();

            foreach (string dir in dirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; locked files are skipped
                }
            }
        }

        /// <summary>
        /// Returns the most recently created shadow copy directory, or
        /// <c>null</c> if none exist. Used for restart-from-last-known-good
        /// recovery when the worker exits unexpectedly.
        /// </summary>
        public string GetLatestShadowCopy()
        {
            if (!Directory.Exists(_shadowRoot))
                return null;

            return Directory.GetDirectories(_shadowRoot)
                .OrderByDescending(d => d)
                .FirstOrDefault();
        }
    }
}
