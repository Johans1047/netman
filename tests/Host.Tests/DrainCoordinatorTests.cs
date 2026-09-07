using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using HotReloadTool.Contracts;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="DrainCoordinator"/> — connection draining via Named Pipes.
    /// TDD RED→GREEN: tests verify timeout behavior and successful drain.
    /// </summary>
    public class DrainCoordinatorTests
    {
        [Fact]
        public void Constructor_NullPipeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new DrainCoordinator(null));
        }

        [Fact]
        public void Drain_NoWorkerListening_ReturnsFalse()
        {
            var coordinator = new DrainCoordinator("nonexistent-pipe-" + Guid.NewGuid().ToString("N"));
            bool result = coordinator.Drain(500);

            Assert.False(result, "Should return false when no worker is listening.");
        }

        [Fact]
        public void Drain_WorkerResponds_ReturnsTrue()
        {
            string pipeName = "test-drain-" + Guid.NewGuid().ToString("N");
            var signal = new ManualResetEventSlim(false);

            // Start a mock worker that responds to drain on a background thread
            var pipeServerThread = new Thread(() =>
            {
                using (var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();

                    // Read drain request
                    byte[] buffer = new byte[4096];
                    int bytesRead = pipeServer.Read(buffer, 0, buffer.Length);

                    // Send drain response
                    var response = new DrainResponseMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true,
                        Message = "draining"
                    };
                    string responseJson = SerializeResponse(response);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    pipeServer.Write(responseBytes, 0, responseBytes.Length);
                    pipeServer.Flush();

                    signal.Set();
                }
            });
            pipeServerThread.IsBackground = true;
            pipeServerThread.Start();

            var coordinator = new DrainCoordinator(pipeName);
            bool result = coordinator.Drain(3000);

            Assert.True(result, "Should return true when worker acknowledges drain.");
            signal.Wait(3000);
        }

        [Fact]
        public void Drain_TimeoutExpires_ReturnsFalse()
        {
            string pipeName = "test-drain-timeout-" + Guid.NewGuid().ToString("N");
            var connectedSignal = new ManualResetEventSlim(false);

            // Start a mock worker that connects but never responds
            var pipeServerThread = new Thread(() =>
            {
                using (var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();
                    connectedSignal.Set();
                    // Simulate a worker that accepts the connection but never responds
                    Thread.Sleep(5000);
                }
            });
            pipeServerThread.IsBackground = true;
            pipeServerThread.Start();

            // Wait for the server to be ready
            connectedSignal.Wait(3000);

            var coordinator = new DrainCoordinator(pipeName);
            bool result = coordinator.Drain(300);

            Assert.False(result, "Should return false when drain times out.");
        }

        private static string SerializeResponse(DrainResponseMessage response)
        {
            return "{\"Protocol\":" + response.Protocol + ",\"Success\":true,\"Message\":\"" + response.Message + "\"}";
        }
    }
}
