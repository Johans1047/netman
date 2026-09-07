using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HotReloadTool.Contracts;
using Newtonsoft.Json;

namespace FixtureWorker
{
    /// <summary>
    /// Fixture worker for integration testing of the state transfer protocol.
    /// Implements <see cref="IStatefulLifecycle"/> and communicates via Named Pipes
    /// using the same protocol as <see cref="HotReloadTool.Host.DrainCoordinator"/>
    /// and <see cref="HotReloadTool.Host.StateTransferHost"/>.
    /// </summary>
    /// <remarks>
    /// Two modes of operation:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Old worker mode</b> (default): listens on the pipe for a drain request,
    ///       responds, then pushes state to the orchestrator's state transfer server.
    ///       Sub-modes: normal, slowdrain, stateless.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>New worker mode</b> (<c>--receive-state</c>): connects to the
    ///       orchestrator's state transfer server, pulls state, writes it to a file.
    ///     </description>
    ///   </item>
    /// </list>
    /// Usage:
    /// <code>
    ///   FixtureWorker.exe &lt;pipeName&gt;[normal|slowdrain|stateless]
    ///   FixtureWorker.exe &lt;pipeName&gt; --receive-state &lt;outputFile&gt;
    /// </code>
    /// </remarks>
    class Program
    {
        private static readonly string InitialState = "{\"Counter\":42,\"Sessions\":[\"s1\",\"s2\"]}";
        private static string _state = InitialState;
        private static readonly object _stateLock = new object();
        private static volatile bool _shouldExit;

        /// <summary>
        /// Application entry point.
        /// </summary>
        /// <param name="args">
        /// Command-line arguments. First arg is always the pipe name.
        /// </param>
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: FixtureWorker <pipeName> [normal|slowdrain|stateless]");
                Console.Error.WriteLine("  or:  FixtureWorker <pipeName> --receive-state <outputFile>");
                return;
            }

            string pipeName = args[0];
            string mode = args.Length > 1 ? args[1] : "normal";

            Console.CancelKeyPress += OnCancelKeyPress;

            if (mode == "--receive-state")
            {
                string outputFile = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), pipeName + ".state.json");
                RunNewWorker(pipeName, outputFile);
            }
            else
            {
                RunOldWorker(pipeName, mode);
            }
        }

        /// <summary>
        /// Handles the Ctrl+C / CTRL_CLOSE_EVENT signal for graceful termination.
        /// </summary>
        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _shouldExit = true;
        }

        /// <summary>
        /// Old worker mode: listens for a drain request on the pipe, responds,
        /// then pushes state to the orchestrator (depending on sub-mode).
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name.</param>
        /// <param name="mode">Sub-mode: normal, slowdrain, or stateless.</param>
        private static void RunOldWorker(string pipeName, string mode)
        {
            Console.WriteLine("FixtureWorker starting. Pipe: " + pipeName + " Mode: " + mode);

            // Step 1: Listen for drain request (we are the server)
            bool drainHandled = false;
            try
            {
                using (var drainPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    Console.WriteLine("Waiting for drain connection...");
                    drainPipe.WaitForConnection();
                    Console.WriteLine("Drain connection accepted.");

                    // Read the drain request message
                    DrainRequestMessage request = ReadMessage<DrainRequestMessage>(drainPipe, 5000);
                    if (request == null)
                    {
                        Console.Error.WriteLine("Failed to read drain request.");
                        return;
                    }

                    Console.WriteLine("Drain request received. Protocol: " + request.Protocol);

                    // Handle slow drain mode — sleep longer than any reasonable timeout
                    if (mode == "slowdrain")
                    {
                        Console.WriteLine("Slow drain: sleeping 10s before responding...");
                        Thread.Sleep(10000);
                    }

                    // Send drain response
                    var response = new DrainResponseMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true,
                        Message = "drained"
                    };

                    try
                    {
                        WriteMessage(drainPipe, response);
                        drainPipe.Flush();
                        Console.WriteLine("Drain response sent.");
                        drainHandled = true;
                    }
                    catch (Exception ex)
                    {
                        // Orchestrator may have already closed the connection due to timeout
                        Console.Error.WriteLine("Drain response failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Drain listener error: " + ex.Message);
            }

            Console.WriteLine("Drain connection closed. Handled: " + drainHandled);

            if (_shouldExit)
            {
                return;
            }

            // Step 2: Push state (only in normal mode with successful drain)
            if (mode == "normal" && drainHandled)
            {
                Console.WriteLine("Pushing state to orchestrator...");
                Thread.Sleep(500); // Allow orchestrator to set up state transfer server

                string stateToPush;
                lock (_stateLock)
                {
                    stateToPush = _state;
                }

                PushStateWithRetry(pipeName, stateToPush, 5000);
            }
            else
            {
                Console.WriteLine("Skipping state push. Mode: " + mode + " DrainHandled: " + drainHandled);
            }

            // Step 3: Wait for orchestrator to terminate this process
            Console.WriteLine("Waiting for termination...");
            Stopwatch sw = Stopwatch.StartNew();
            while (!_shouldExit && sw.ElapsedMilliseconds < 30000)
            {
                Thread.Sleep(500);
            }

            Console.WriteLine("Exiting.");
        }

        /// <summary>
        /// New worker mode: connects to the orchestrator's state transfer server,
        /// pulls state, and writes it to a file for test verification.
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name.</param>
        /// <param name="outputFile">File path where received state is written.</param>
        private static void RunNewWorker(string pipeName, string outputFile)
        {
            Console.WriteLine("New worker starting. Pipe: " + pipeName + " Output: " + outputFile);

            // Allow orchestrator to set up the state transfer server
            Thread.Sleep(500);

            string receivedState = ReceiveState(pipeName, 5000);
            string output = receivedState ?? "NULL";

            try
            {
                File.WriteAllText(outputFile, output);
                Console.WriteLine("State written to file: " + output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to write state file: " + ex.Message);
            }

            // Stay alive so the test can verify process state
            Stopwatch sw = Stopwatch.StartNew();
            while (!_shouldExit && sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(500);
            }

            Console.WriteLine("New worker exiting.");
        }

        /// <summary>
        /// Pushes state to the orchestrator's state transfer server with retry.
        /// Called by the old worker after drain completes.
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name.</param>
        /// <param name="state">JSON state to push.</param>
        /// <param name="timeoutMilliseconds">Total time to keep retrying.</param>
        private static void PushStateWithRetry(string pipeName, string state, int timeoutMilliseconds)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMilliseconds && !_shouldExit)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                    {
                        pipe.Connect(1000);
                        Console.WriteLine("Connected to state transfer server.");

                        var message = new StateTransferMessage
                        {
                            Protocol = ReloadMessages.ProtocolVersion,
                            StateJson = state
                        };
                        WriteMessage(pipe, message);
                        pipe.Flush();

                        StateAckMessage ack = ReadMessage<StateAckMessage>(pipe, 2000);
                        if (ack != null && ack.Success)
                        {
                            Console.WriteLine("State pushed successfully.");
                            return;
                        }

                        Console.WriteLine("State push ack was not success. Retrying...");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("State push attempt failed: " + ex.Message);
                    Thread.Sleep(200);
                }
            }

            Console.Error.WriteLine("State push timed out after " + timeoutMilliseconds + "ms.");
        }

        /// <summary>
        /// Pulls state from the orchestrator's state transfer server.
        /// Called by the new worker during initialization.
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name.</param>
        /// <param name="timeoutMilliseconds">Timeout for the operation.</param>
        /// <returns>The state JSON, or null if no state was transferred.</returns>
        private static string ReceiveState(string pipeName, int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMilliseconds);
                    Console.WriteLine("Connected to state transfer server (pull).");

                    // Send pull request (StateJson = null)
                    var request = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = null
                    };
                    WriteMessage(pipe, request);
                    pipe.Flush();

                    // Read state transfer response
                    StateTransferMessage response = ReadMessage<StateTransferMessage>(pipe, timeoutMilliseconds);
                    if (response == null)
                    {
                        Console.WriteLine("No state received (null response).");
                        return null;
                    }

                    // Send ack
                    var ack = new StateAckMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true
                    };
                    WriteMessage(pipe, ack);
                    pipe.Flush();

                    Console.WriteLine("State pulled: " + response.StateJson);
                    return response.StateJson;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Receive state failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Serializes a message to JSON and writes it to the pipe.
        /// </summary>
        /// <typeparam name="T">Message type.</typeparam>
        /// <param name="pipe">Pipe stream to write to.</param>
        /// <param name="message">Message to serialize and write.</param>
        private static void WriteMessage<T>(PipeStream pipe, T message)
        {
            string json = JsonConvert.SerializeObject(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }

        /// <summary>
        /// Reads a JSON message from the pipe and deserializes it.
        /// Uses a background thread with a timeout to avoid blocking indefinitely.
        /// </summary>
        /// <typeparam name="T">Message type to deserialize.</typeparam>
        /// <param name="pipe">Pipe stream to read from.</param>
        /// <param name="timeoutMilliseconds">Maximum time to wait for the read.</param>
        /// <returns>The deserialized message, or default(T) on timeout/error.</returns>
        private static T ReadMessage<T>(PipeStream pipe, int timeoutMilliseconds) where T : new()
        {
            byte[] buffer = new byte[65536];

            try
            {
                Task<int> readTask = Task<int>.Factory.FromAsync(
                    pipe.BeginRead, pipe.EndRead,
                    buffer, 0, buffer.Length, null);

                if (!readTask.Wait(timeoutMilliseconds))
                    return default(T);

                int bytesRead = readTask.Result;
                if (bytesRead <= 0)
                    return default(T);

                string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return default(T);
            }
        }
    }
}
