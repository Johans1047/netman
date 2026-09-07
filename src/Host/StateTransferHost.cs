using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HotReloadTool.Contracts;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Named Pipes server that receives state from the old worker during drain,
    /// holds it in memory, and delivers it to the new worker on initialization.
    /// Implements the state transfer protocol:
    /// <list type="bullet">
    ///   <item><description>Old worker pushes state → Host stores it</description></item>
    ///   <item><description>New worker pulls state → Host delivers it (or null for stateless)</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Thread-safe: <see cref="BeginReceive"/> and <see cref="DeliverState"/> can be
    /// called from different threads. The server processes one connection at a time.
    /// </remarks>
    public class StateTransferHost : IDisposable, IStateTransferHost
    {
        private readonly string _pipeName;
        private readonly int _defaultTimeoutMilliseconds;
        private string _heldState;
        private bool _disposed;
        private NamedPipeServerStream _pipeServer;

        /// <summary>
        /// Creates a new <see cref="StateTransferHost"/>.
        /// </summary>
        /// <param name="pipeName">
        /// Named Pipes endpoint name. Must match the worker's pipe name.
        /// </param>
        /// <param name="defaultTimeoutMilliseconds">
        /// Default timeout for pipe operations. Default is 5000ms.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pipeName"/> is null or empty.
        /// </exception>
        public StateTransferHost(string pipeName, int defaultTimeoutMilliseconds = 5000)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("pipeName is required.", "pipeName");

            _pipeName = pipeName;
            _defaultTimeoutMilliseconds = defaultTimeoutMilliseconds;
        }

        /// <summary>
        /// Begins listening for the old worker to push its state. Blocks until
        /// the worker connects and sends its state, or until the timeout expires.
        /// </summary>
        /// <param name="timeoutMilliseconds">
        /// Maximum time to wait for the old worker to connect and send state.
        /// Uses the default timeout if not specified.
        /// </param>
        /// <returns>
        /// The state JSON string from the old worker, or <c>null</c> if the
        /// operation timed out or no state was transferred.
        /// </returns>
        public string BeginReceive(int timeoutMilliseconds = 0)
        {
            if (timeoutMilliseconds <= 0)
                timeoutMilliseconds = _defaultTimeoutMilliseconds;

            try
            {
                _pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1);
                if (!_pipeServer.WaitForConnectionAsync(timeoutMilliseconds))
                {
                    return null;
                }

                // Read the state transfer message
                StateTransferMessage message = ReadMessage<StateTransferMessage>(_pipeServer, timeoutMilliseconds);
                if (message == null)
                {
                    // Send nack
                    WriteMessage(_pipeServer, new StateAckMessage { Protocol = ReloadMessages.ProtocolVersion, Success = false, Error = "timeout" });
                    return null;
                }

                // Store the state
                _heldState = message.StateJson;

                // Send ack
                WriteMessage(_pipeServer, new StateAckMessage { Protocol = ReloadMessages.ProtocolVersion, Success = true });

                return _heldState;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                if (_pipeServer != null)
                {
                    _pipeServer.Dispose();
                    _pipeServer = null;
                }
            }
        }

        /// <summary>
        /// Delivers the held state to the new worker. Blocks until the new worker
        /// connects and pulls the state, or until the timeout expires.
        /// </summary>
        /// <param name="timeoutMilliseconds">
        /// Maximum time to wait for the new worker to connect and pull state.
        /// Uses the default timeout if not specified.
        /// </param>
        /// <returns>
        /// <c>true</c> if the state was delivered successfully; <c>false</c> if
        /// the operation timed out.
        /// </returns>
        public bool DeliverState(int timeoutMilliseconds = 0)
        {
            if (timeoutMilliseconds <= 0)
                timeoutMilliseconds = _defaultTimeoutMilliseconds;

            try
            {
                _pipeServer = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1);
                if (!_pipeServer.WaitForConnectionAsync(timeoutMilliseconds))
                {
                    return false;
                }

                // Read the pull request
                StateTransferMessage request = ReadMessage<StateTransferMessage>(_pipeServer, timeoutMilliseconds);
                if (request == null)
                {
                    return false;
                }

                // Send the held state (or null for stateless restart)
                WriteMessage(_pipeServer, new StateTransferMessage
                {
                    Protocol = ReloadMessages.ProtocolVersion,
                    StateJson = _heldState
                });

                // Read ack
                StateAckMessage ack = ReadMessage<StateAckMessage>(_pipeServer, timeoutMilliseconds);
                return ack != null && ack.Success;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            finally
            {
                if (_pipeServer != null)
                {
                    _pipeServer.Dispose();
                    _pipeServer = null;
                }
            }
        }

        /// <summary>
        /// Gets the currently held state JSON, or <c>null</c> if no state has been received.
        /// </summary>
        public string HeldState
        {
            get { return _heldState; }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_pipeServer != null)
            {
                try { _pipeServer.Dispose(); } catch { }
                _pipeServer = null;
            }
        }

        private static void WriteMessage<T>(NamedPipeServerStream pipe, T message)
        {
            string json = JsonHelpers.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }

        private static T ReadMessage<T>(NamedPipeServerStream pipe, int timeoutMilliseconds) where T : new()
        {
            byte[] buffer = new byte[65536];
            Task<int> readTask = Task<int>.Factory.FromAsync(
                pipe.BeginRead, pipe.EndRead,
                buffer, 0, buffer.Length, null);

            if (!readTask.Wait(timeoutMilliseconds))
                return default(T);

            string json = Encoding.UTF8.GetString(buffer, 0, readTask.Result);
            return JsonHelpers.Deserialize<T>(json);
        }
    }

    /// <summary>
    /// JSON serialization helpers using Newtonsoft.Json.
    /// Shared between StateTransferHost and other Host components.
    /// </summary>
    internal static class JsonHelpers
    {
        public static string Serialize<T>(T obj)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(obj);
        }

        public static T Deserialize<T>(string json)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
        }
    }

    /// <summary>
    /// Extension method for NamedPipeServerStream to support wait with timeout.
    /// Uses a background thread to avoid requiring PipeOptions.Asynchronous.
    /// </summary>
    internal static class PipeExtensions
    {
        public static bool WaitForConnectionAsync(this NamedPipeServerStream pipe, int timeoutMilliseconds)
        {
            var waitHandle = new ManualResetEvent(false);
            var waitThread = new Thread(() =>
            {
                try
                {
                    pipe.WaitForConnection();
                    waitHandle.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Pipe was disposed before connection
                }
            });
            waitThread.IsBackground = true;
            waitThread.Start();
            return waitHandle.WaitOne(timeoutMilliseconds);
        }
    }
}
