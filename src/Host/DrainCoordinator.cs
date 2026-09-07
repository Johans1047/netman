using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using HotReloadTool.Contracts;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Coordinates connection draining for the old Worker before termination.
    /// Sends a <see cref="DrainRequestMessage"/> via Named Pipes and waits for
    /// the worker to complete in-flight HTTP requests and WebSocket close
    /// handshakes. If draining does not complete within the configured timeout,
    /// the caller should force-terminate the worker.
    /// </summary>
    /// <remarks>
    /// WebSocket draining MUST NOT cancel the ReceiveAsync CancellationToken
    /// until the close frame exchange completes — otherwise clients receive
    /// an abrupt disconnect. The worker is responsible for this; the
    /// DrainCoordinator only signals and waits.
    /// </remarks>
    public class DrainCoordinator : IDrainCoordinator
    {
        private readonly string _pipeName;

        /// <summary>
        /// Creates a new <see cref="DrainCoordinator"/>.
        /// </summary>
        /// <param name="pipeName">
        /// Named Pipes endpoint name. Must match the worker's pipe name.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pipeName"/> is null or empty.
        /// </exception>
        public DrainCoordinator(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("pipeName is required.", "pipeName");

            _pipeName = pipeName;
        }

        /// <summary>
        /// Sends a drain request to the worker and waits for acknowledgment.
        /// Returns <c>true</c> if the worker acknowledged the drain within
        /// the timeout; <c>false</c> if the pipe connection or response timed out.
        /// </summary>
        /// <param name="timeoutMilliseconds">
        /// Maximum time to wait for the worker to acknowledge the drain request.
        /// </param>
        /// <returns>
        /// <c>true</c> if drain was acknowledged; <c>false</c> on timeout or error.
        /// </returns>
        public bool Drain(int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMilliseconds);

                    // Send drain request
                    var request = new DrainRequestMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion
                    };
                    string requestJson = JsonHelpers.Serialize(request);
                    byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);
                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.Flush();

                    // Read drain response with timeout (NamedPipeClientStream does not
                    // support ReadTimeout, so wrap the read in a Task with Wait).
                    byte[] buffer = new byte[4096];
                    Task<int> readTask = Task<int>.Factory.FromAsync(
                        pipe.BeginRead, pipe.EndRead,
                        buffer, 0, buffer.Length, null);

                    if (!readTask.Wait(timeoutMilliseconds))
                        return false;

                    string responseJson = Encoding.UTF8.GetString(buffer, 0, readTask.Result);
                    var response = JsonHelpers.Deserialize<DrainResponseMessage>(responseJson);

                    return response != null && response.Success;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                // Pipe not available or broken
                return false;
            }
        }
    }
}
