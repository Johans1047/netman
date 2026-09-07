namespace HotReloadTool.Contracts
{
    /// <summary>
    /// Represents the runtime state of a Worker process from the Host's perspective.
    /// </summary>
    public enum ProcessState
    {
        /// <summary>Worker has not been launched yet.</summary>
        Idle = 0,

        /// <summary>Worker process started, waiting for readiness signal.</summary>
        Starting,

        /// <summary>Worker is running and accepting connections.</summary>
        Running,

        /// <summary>Drain requested; worker is completing in-flight work.</summary>
        Draining,

        /// <summary>State transfer to new worker in progress.</summary>
        TransferringState,

        /// <summary>Termination signal sent; awaiting clean exit.</summary>
        Terminating,

        /// <summary>Worker exited (cleanly or via Kill).</summary>
        Exited,

        /// <summary>Worker exited unexpectedly (crash) without Host initiating termination.</summary>
        Crashed
    }
}
