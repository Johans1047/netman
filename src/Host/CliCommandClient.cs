using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Named Pipes client that sends CLI control commands (stop, status) to a running Host.
    /// </summary>
    public class CliCommandClient : IDisposable
    {
        private readonly string _pipeName;

        /// <summary>
        /// Creates a new <see cref="CliCommandClient"/>.
        /// </summary>
        /// <param name="pipeName">Named Pipes endpoint name (config pipe name + "-cli").</param>
        public CliCommandClient(string pipeName)
        {
            _pipeName = pipeName;
        }

        /// <summary>
        /// Sends a command to the running Host and returns the response.
        /// Returns null if the Host is not running or the connection failed.
        /// </summary>
        public CliResponseMessage SendCommand(string command, int timeoutMilliseconds = 5000)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMilliseconds);

                    // Send request
                    var request = new CliRequestMessage { Command = command };
                    string requestJson = JsonConvert.SerializeObject(request);
                    byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);
                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.Flush();

                    // Read response
                    byte[] buffer = new byte[4096];
                    int bytesRead = pipe.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                        return null;

                    string responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    return JsonConvert.DeserializeObject<CliResponseMessage>(responseJson);
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

        public void Dispose() { }
    }
}
