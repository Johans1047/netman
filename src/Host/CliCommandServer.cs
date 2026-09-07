using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Named Pipes server that listens for CLI control commands (stop, status) from
    /// external processes. Runs in the background while the Host is active.
    /// </summary>
    public class CliCommandServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<CliRequestMessage, CliResponseMessage> _handler;
        private Thread _listenerThread;
        private bool _disposed;
        private readonly object _sync = new object();

        /// <summary>
        /// Creates a new <see cref="CliCommandServer"/>.
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name (uses config pipe name + "-cli").</param>
        /// <param name="handler">Callback that processes requests and returns responses.</param>
        public CliCommandServer(string pipeName, Func<CliRequestMessage, CliResponseMessage> handler)
        {
            _pipeName = pipeName;
            _handler = handler;
        }

        /// <summary>
        /// Starts the listener on a background thread.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException("CliCommandServer");

                _listenerThread = new Thread(ListenLoop);
                _listenerThread.IsBackground = true;
                _listenerThread.Start();
            }
        }

        /// <summary>
        /// Stops the listener.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }

        private void ListenLoop()
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_disposed)
                        return;
                }

                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1);

                    // Block until a client connects
                    // WaitForConnection() in .NETFX 4.5.1 has no timeout overload,
                    // so we block the thread here — Dispose() from Stop() unblocks it.
                    pipe.WaitForConnection();

                    // Read request
                    CliRequestMessage request = ReadMessage(pipe);
                    if (request == null)
                        continue;

                    // Process
                    CliResponseMessage response = _handler(request);

                    // Write response
                    WriteMessage(pipe, response);
                }
                catch (ObjectDisposedException)
                {
                    // Pipe was disposed by Stop() — exit gracefully
                    return;
                }
                catch (Exception)
                {
                    // Listener error — brief pause before retry
                    Thread.Sleep(200);
                }
                finally
                {
                    if (pipe != null)
                    {
                        try { pipe.Dispose(); } catch { }
                    }
                }
            }
        }

        private static CliRequestMessage ReadMessage(PipeStream pipe)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int bytesRead = pipe.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                    return null;
                string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                return JsonConvert.DeserializeObject<CliRequestMessage>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteMessage(PipeStream pipe, CliResponseMessage message)
        {
            try
            {
                string json = JsonConvert.SerializeObject(message);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
