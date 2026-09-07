using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using HotReloadTool.Contracts;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="StateTransferHost"/> — Named Pipes server for state transfer.
    /// TDD RED→GREEN: tests verify receive/deliver protocol, timeout, and round-trip.
    /// </summary>
    public class StateTransferHostTests
    {
        [Fact]
        public void Constructor_NullPipeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new StateTransferHost(null));
        }

        [Fact]
        public void Constructor_EmptyPipeName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new StateTransferHost(""));
        }

        [Fact]
        public void BeginReceive_NoWorkerConnects_ReturnsNull()
        {
            var host = new StateTransferHost("test-host-timeout-" + Guid.NewGuid().ToString("N"), 300);
            string result = host.BeginReceive();

            Assert.Null(result);
        }

        [Fact]
        public void BeginReceive_WorkerPushesState_ReturnsStateJson()
        {
            string pipeName = "test-host-receive-" + Guid.NewGuid().ToString("N");
            var host = new StateTransferHost(pipeName, 3000);
            var signal = new ManualResetEventSlim(false);

            // Start a mock worker that pushes state
            var workerThread = new Thread(() =>
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);

                    // Send state transfer message
                    var message = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = "{\"sessionCount\":5}"
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(message);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    // Read ack
                    byte[] ackBuffer = new byte[4096];
                    pipe.Read(ackBuffer, 0, ackBuffer.Length);

                    signal.Set();
                }
            });
            workerThread.IsBackground = true;
            workerThread.Start();

            string result = host.BeginReceive();

            Assert.Equal("{\"sessionCount\":5}", result);
            Assert.Equal("{\"sessionCount\":5}", host.HeldState);
            signal.Wait(3000);
        }

        [Fact]
        public void DeliverState_NoWorkerConnects_ReturnsFalse()
        {
            string pipeName = "test-host-deliver-timeout-" + Guid.NewGuid().ToString("N");
            var host = new StateTransferHost(pipeName, 300);
            var field = host.GetType().GetField("_heldState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
                field.SetValue(host, "{\"data\":1}");

            bool result = host.DeliverState();

            Assert.False(result, "Should return false when no worker connects within timeout.");
        }

        [Fact]
        public void DeliverState_WorkerPullsState_ReturnsTrue()
        {
            string pipeName = "test-host-deliver-" + Guid.NewGuid().ToString("N");
            var host = new StateTransferHost(pipeName, 3000);

            // Set held state via reflection (simulating prior BeginReceive)
            var heldStateField = host.GetType().GetField("_heldState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (heldStateField != null)
                heldStateField.SetValue(host, "{\"data\":1}");

            var signal = new ManualResetEventSlim(false);

            // Start a mock worker that pulls state
            var workerThread = new Thread(() =>
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);

                    // Send pull request
                    var request = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = null
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    // Read state transfer response
                    byte[] buffer = new byte[4096];
                    int bytesRead = pipe.Read(buffer, 0, buffer.Length);
                    string responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var response = Newtonsoft.Json.JsonConvert.DeserializeObject<StateTransferMessage>(responseJson);

                    Assert.Equal("{\"data\":1}", response.StateJson);

                    // Send ack
                    var ack = new StateAckMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true
                    };
                    string ackJson = Newtonsoft.Json.JsonConvert.SerializeObject(ack);
                    byte[] ackBytes = Encoding.UTF8.GetBytes(ackJson);
                    pipe.Write(ackBytes, 0, ackBytes.Length);
                    pipe.Flush();

                    signal.Set();
                }
            });
            workerThread.IsBackground = true;
            workerThread.Start();

            bool result = host.DeliverState();

            Assert.True(result, "Should return true when worker pulls state successfully.");
            signal.Wait(3000);
        }

        [Fact]
        public void RoundTrip_OldWorkerPushesNewWorkerPulls_StateIntact()
        {
            string pipeName = "test-roundtrip-" + Guid.NewGuid().ToString("N");
            string originalState = "{\"users\":[\"alice\",\"bob\"],\"counter\":99}";

            // Step 1: Host receives state from old worker
            var host = new StateTransferHost(pipeName, 3000);

            var oldWorkerThread = new Thread(() =>
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);
                    var message = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = originalState
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(message);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    byte[] ackBuffer = new byte[4096];
                    pipe.Read(ackBuffer, 0, ackBuffer.Length);
                }
            });
            oldWorkerThread.IsBackground = true;
            oldWorkerThread.Start();

            string receivedState = host.BeginReceive();
            oldWorkerThread.Join(3000);

            Assert.Equal(originalState, receivedState);

            // Step 2: Host delivers state to new worker
            var newWorkerSignal = new ManualResetEventSlim(false);
            string pulledState = null;

            var newWorkerThread = new Thread(() =>
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);

                    var request = new StateTransferMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        StateJson = null
                    };
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    byte[] buffer = new byte[4096];
                    int bytesRead = pipe.Read(buffer, 0, buffer.Length);
                    string responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var response = Newtonsoft.Json.JsonConvert.DeserializeObject<StateTransferMessage>(responseJson);
                    pulledState = response.StateJson;

                    var ack = new StateAckMessage
                    {
                        Protocol = ReloadMessages.ProtocolVersion,
                        Success = true
                    };
                    string ackJson = Newtonsoft.Json.JsonConvert.SerializeObject(ack);
                    byte[] ackBytes = Encoding.UTF8.GetBytes(ackJson);
                    pipe.Write(ackBytes, 0, ackBytes.Length);
                    pipe.Flush();

                    newWorkerSignal.Set();
                }
            });
            newWorkerThread.IsBackground = true;
            newWorkerThread.Start();

            bool delivered = host.DeliverState();
            newWorkerSignal.Wait(3000);

            Assert.True(delivered, "State should be delivered successfully.");
            Assert.Equal(originalState, pulledState);
        }
    }
}
