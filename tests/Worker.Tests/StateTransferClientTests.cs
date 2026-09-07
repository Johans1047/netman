using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using HotReloadTool.Contracts;
using Xunit;

namespace HotReloadTool.Worker.Tests
{
    /// <summary>
    /// Tests for <see cref="StateTransferClient"/> — Named Pipes client for state transfer.
    /// TDD RED→GREEN: tests verify push/pull protocol, timeout, and round-trip.
    /// </summary>
    public class StateTransferClientTests
    {
        [Fact]
        public void Constructor_NullPipeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new StateTransferClient(null));
        }

        [Fact]
        public void Constructor_EmptyPipeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new StateTransferClient(""));
        }

        [Fact]
        public void SendState_NoHostListening_ReturnsFalse()
        {
            var client = new StateTransferClient("nonexistent-pipe-" + Guid.NewGuid().ToString("N"));
            bool result = client.SendState("{\"key\":\"value\"}", 500);

            Assert.False(result, "Should return false when no host is listening.");
        }

        [Fact]
        public void ReceiveState_NoHostListening_ReturnsNull()
        {
            var client = new StateTransferClient("nonexistent-pipe-" + Guid.NewGuid().ToString("N"));
            string result = client.ReceiveState(500);

            Assert.Null(result);
        }

        [Fact]
        public void SendState_HostAcknowledges_ReturnsTrue()
        {
            string pipeName = "test-client-send-" + Guid.NewGuid().ToString("N");
            var signal = new ManualResetEventSlim(false);

            // Start a mock host that acknowledges
            var hostThread = new Thread(() =>
            {
                using (var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();

                    // Read state transfer message
                    byte[] buffer = new byte[4096];
                    pipeServer.Read(buffer, 0, buffer.Length);

                    // Send ack
                    var ack = new StateAckMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true
                    };
                    string ackJson = Newtonsoft.Json.JsonConvert.SerializeObject(ack);
                    byte[] ackBytes = Encoding.UTF8.GetBytes(ackJson);
                    pipeServer.Write(ackBytes, 0, ackBytes.Length);
                    pipeServer.Flush();

                    signal.Set();
                }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            var client = new StateTransferClient(pipeName);
            bool result = client.SendState("{\"key\":\"value\"}", 3000);

            Assert.True(result, "Should return true when host acknowledges.");
            signal.Wait(3000);
        }

        [Fact]
        public void ReceiveState_HostDeliversState_ReturnsStateJson()
        {
            string pipeName = "test-client-receive-" + Guid.NewGuid().ToString("N");
            var signal = new ManualResetEventSlim(false);

            // Start a mock host that delivers state
            var hostThread = new Thread(() =>
            {
                using (var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();

                    // Read pull request
                    byte[] buffer = new byte[4096];
                    int bytesRead = pipeServer.Read(buffer, 0, buffer.Length);

                    // Send state transfer message with state
                    var response = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = "{\"counter\":42}"
                    };
                    string responseJson = Newtonsoft.Json.JsonConvert.SerializeObject(response);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    pipeServer.Write(responseBytes, 0, responseBytes.Length);
                    pipeServer.Flush();

                    // Read ack
                    byte[] ackBuffer = new byte[4096];
                    pipeServer.Read(ackBuffer, 0, ackBuffer.Length);

                    signal.Set();
                }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            var client = new StateTransferClient(pipeName);
            string result = client.ReceiveState(3000);

            Assert.Equal("{\"counter\":42}", result);
            signal.Wait(3000);
        }

        [Fact]
        public void ReceiveState_HostDeliversNull_ReturnsNull()
        {
            string pipeName = "test-client-null-" + Guid.NewGuid().ToString("N");
            var signal = new ManualResetEventSlim(false);

            // Start a mock host that delivers null (stateless restart)
            var hostThread = new Thread(() =>
            {
                using (var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();

                    // Read pull request
                    byte[] buffer = new byte[4096];
                    pipeServer.Read(buffer, 0, buffer.Length);

                    // Send state transfer message with null state
                    var response = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = null
                    };
                    string responseJson = Newtonsoft.Json.JsonConvert.SerializeObject(response);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    pipeServer.Write(responseBytes, 0, responseBytes.Length);
                    pipeServer.Flush();

                    // Read ack
                    byte[] ackBuffer = new byte[4096];
                    pipeServer.Read(ackBuffer, 0, ackBuffer.Length);

                    signal.Set();
                }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            var client = new StateTransferClient(pipeName);
            string result = client.ReceiveState(3000);

            Assert.Null(result);
            signal.Wait(3000);
        }
    }
}
