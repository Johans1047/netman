using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Manages the Worker process lifecycle: launch from a shadow copy directory,
    /// graceful termination via CTRL_CLOSE_EVENT with Kill() fallback, and
    /// unexpected exit detection.
    /// </summary>
    /// <remarks>
    /// CTRL_CLOSE_EVENT is sent via P/Invoke to <c>GenerateConsoleCtrlEvent</c>.
    /// If the worker does not exit within the configured timeout, <see cref="Process.Kill"/>
    /// is called as a last resort. Workers that ignore CTRL_CLOSE will be force-killed.
    /// </remarks>
    public class ProcessManager : IDisposable, IProcessManager
    {
        private Process _process;
        private bool _disposed;

        /// <summary>
        /// The underlying <see cref="Process"/> instance, or <c>null</c> if no
        /// worker is currently running.
        /// </summary>
        public Process Current { get { return _process; } }

        /// <summary>
        /// <c>true</c> when a worker process is running and has not exited.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                return _process != null && !_process.HasExited;
            }
        }

        /// <summary>
        /// Launches a new Worker process from the specified executable path.
        /// Sets WorkingDirectory to the executable's directory so that
        /// configuration files resolve correctly.
        /// </summary>
        /// <param name="executablePath">Absolute path to the Worker .exe.</param>
        /// <param name="arguments">Optional command-line arguments.</param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when <paramref name="executablePath"/> does not exist.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a worker is already running.
        /// </exception>
        public void Launch(string executablePath, string arguments = null)
        {
            if (!File.Exists(executablePath))
                throw new FileNotFoundException("Worker executable not found.", executablePath);

            if (IsRunning)
                throw new InvalidOperationException("Worker process is already running.");

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = false
            };

            if (arguments != null)
                startInfo.Arguments = arguments;

            _process = new Process { StartInfo = startInfo };
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
            _process.Start();
        }

        /// <summary>
        /// Attempts graceful termination by sending CTRL_CLOSE_EVENT. Waits up to
        /// <paramref name="timeoutMilliseconds"/> for the worker to exit. If it
        /// does not exit, calls <see cref="Process.Kill"/> as a fallback.
        /// </summary>
        /// <param name="timeoutMilliseconds">
        /// Time to wait after CTRL_CLOSE_EVENT before Kill. If 0, Kill is immediate.
        /// </param>
        /// <returns>
        /// <c>true</c> if the worker exited gracefully; <c>false</c> if Kill was required.
        /// </returns>
        public bool TerminateGracefully(int timeoutMilliseconds)
        {
            if (_process == null || _process.HasExited)
                return true;

            bool graceful = false;

            if (timeoutMilliseconds <= 0)
            {
                // Immediate kill path
                KillWorker();
                return false;
            }

            try
            {
                // Send CTRL_CLOSE_EVENT to the worker process
                GenerateConsoleCtrlEvent(CtrlCloseEvent, (uint)_process.Id);
                graceful = _process.WaitForExit(timeoutMilliseconds);
            }
            catch (Win32Exception)
            {
                // Process already exited or cannot receive signal
            }
            catch (InvalidOperationException)
            {
                // Process already exited
            }

            if (!graceful && !_process.HasExited)
            {
                KillWorker();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Forcefully terminates the worker process via <see cref="Process.Kill"/>.
        /// </summary>
        public void KillWorker()
        {
            if (_process == null || _process.HasExited)
                return;

            try
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already exited
            }
        }

        /// <summary>
        /// Raises when the worker process exits (cleanly or via Kill).
        /// </summary>
        public event EventHandler Exited;

        private void OnProcessExited(object sender, EventArgs e)
        {
            if (Exited != null)
                Exited(this, e);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_process != null)
            {
                _process.Exited -= OnProcessExited;
                KillWorker();
                _process.Dispose();
                _process = null;
            }
        }

        // Win32 P/Invoke for CTRL_CLOSE_EVENT
        private const uint CtrlCloseEvent = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
    }
}
