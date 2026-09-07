namespace HotReloadTool.Contracts
{
    /// <summary>
    /// Protocol constants and IPC message DTOs for the Hot Reload tool's
    /// Named Pipes communication between Host and Worker.
    /// </summary>
    /// <remarks>
    /// All messages are serialized as JSON via Newtonsoft.Json.
    /// The <see cref="ProtocolVersion">Protocol</see> field MUST be present in
    /// every message for forward-compatibility.
    /// </remarks>
    public static class ReloadMessages
    {
        /// <summary>Current IPC protocol version. Host and Worker MUST agree.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Default Named Pipes endpoint name for state transfer.</summary>
        public const string DefaultPipeName = "hotreload-state";
    }

    /// <summary>
    /// Host → Worker: request the worker to begin draining connections.
    /// Worker SHOULD stop accepting new requests and initiate graceful closes.
    /// </summary>
    public class DrainRequestMessage
    {
        /// <summary>Protocol version. Must equal <see cref="ReloadMessages.ProtocolVersion"/>.</summary>
        public int Protocol { get; set; }
    }

    /// <summary>
    /// Worker → Host: response to a <see cref="DrainRequestMessage"/>.
    /// Indicates whether the worker has acknowledged the drain request.
    /// </summary>
    public class DrainResponseMessage
    {
        /// <summary>Protocol version.</summary>
        public int Protocol { get; set; }

        /// <summary>
        /// <c>true</c> if the worker acknowledged the drain and is stopping
        /// new connections; <c>false</c> if the drain could not be initiated.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>Human-readable status or error message.</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// Host → Worker: graceful exit signal. Worker SHOULD perform cleanup and exit.
    /// If the worker does not exit within the configured timeout, the Host will
    /// fall back to <c>Process.Kill()</c>.
    /// </summary>
    public class ExitGracefullyMessage
    {
        /// <summary>Protocol version.</summary>
        public int Protocol { get; set; }

        /// <summary>Reason for termination (logged by worker).</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// Host → New Worker: state transfer payload containing the JSON snapshot
    /// captured from the previous worker. May be <c>null</c> for stateless restarts.
    /// </summary>
    public class StateTransferMessage
    {
        /// <summary>Protocol version.</summary>
        public int Protocol { get; set; }

        /// <summary>
        /// JSON-serialized state from the previous worker, or <c>null</c> if
        /// no state was transferred (stateless restart).
        /// </summary>
        public string StateJson { get; set; }
    }

    /// <summary>
    /// Worker → Host: acknowledgment of a <see cref="StateTransferMessage"/>.
    /// </summary>
    public class StateAckMessage
    {
        /// <summary>Protocol version.</summary>
        public int Protocol { get; set; }

        /// <summary>
        /// <c>true</c> if the worker successfully ingested the state snapshot.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>Error message if state ingestion failed.</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Discriminator constants for message type routing over Named Pipes.
    /// Used to identify message kind without full deserialization.
    /// </summary>
    public static class MessageTypes
    {
        public const string DrainRequest = "DrainRequest";
        public const string DrainResponse = "DrainResponse";
        public const string ExitGracefully = "ExitGracefully";
        public const string StateTransfer = "StateTransfer";
        public const string StateAck = "StateAck";
    }
}
