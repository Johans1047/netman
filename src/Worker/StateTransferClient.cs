using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using HotReloadTool.Contracts;

namespace HotReloadTool.Worker
{
    /// <summary>
    /// Named Pipes client that Workers use to participate in state transfer
    /// across hot-reload restarts. The old worker calls <see cref="SendState"/>
    /// to push its state JSON to the Host; the new worker calls
    /// <see cref="ReceiveState"/> to pull the stored state from the Host.
    /// </summary>
    /// <remarks>
    /// Protocol:
    /// <list type="bullet">
    ///   <item><description>Client connects to Host's Named Pipes server</description></item>
    ///   <item><description>Client sends <see cref="StateTransferMessage"/> (StateJson=null means "pull")</description></item>
    ///   <item><description>For push: Host stores state, responds with <see cref="StateAckMessage"/></description></item>
    ///   <item><description>For pull: Host responds with <see cref="StateTransferMessage"/> containing stored state</description></item>
    /// </list>
    /// </remarks>
    public class StateTransferClient
    {
        private readonly string _pipeName;

        /// <summary>
        /// Creates a new <see cref="StateTransferClient"/>.
        /// </summary>
        /// <param name="pipeName">
        /// Named Pipes endpoint name. Must match the Host's pipe name.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pipeName"/> is null or empty.
        /// </exception>
        public StateTransferClient(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("pipeName is required.", "pipeName");

            _pipeName = pipeName;
        }

        /// <summary>
        /// Sends the worker's state JSON to the Host. Called by the old worker
        /// during the drain phase.
        /// </summary>
        /// <param name="stateJson">JSON snapshot of the worker's state.</param>
        /// <param name="timeoutMilliseconds">
        /// Maximum time to wait for the Host to acknowledge.
        /// </param>
        /// <returns>
        /// <c>true</c> if the Host acknowledged receipt; <c>false</c> on timeout or error.
        /// </returns>
        public bool SendState(string stateJson, int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMilliseconds);

                    // Send state transfer message (push)
                    var message = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = stateJson
                    };
                    WriteMessage(pipe, message);

                    // Read ack with timeout
                    StateAckMessage ack = ReadMessage<StateAckMessage>(pipe, timeoutMilliseconds);
                    return ack != null && ack.Success;
                }
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Receives the state JSON from the Host. Called by the new worker
        /// during initialization.
        /// </summary>
        /// <param name="timeoutMilliseconds">
        /// Maximum time to wait for the Host to send state.
        /// </param>
        /// <returns>
        /// The state JSON string, or <c>null</c> if no state was transferred
        /// (stateless restart) or the operation timed out.
        /// </returns>
        public string ReceiveState(int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMilliseconds);

                    // Send pull request (StateJson = null)
                    var request = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = null
                    };
                    WriteMessage(pipe, request);

                    // Read state transfer response with timeout
                    StateTransferMessage response = ReadMessage<StateTransferMessage>(pipe, timeoutMilliseconds);
                    if (response == null)
                        return null;

                    // Send ack
                    var ack = new StateAckMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true
                    };
                    WriteMessage(pipe, ack);

                    return response.StateJson;
                }
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void WriteMessage<T>(NamedPipeClientStream pipe, T message)
        {
            string json = JsonHelpers.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }

        private static T ReadMessage<T>(NamedPipeClientStream pipe, int timeoutMilliseconds) where T : new()
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
}
