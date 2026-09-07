namespace HotReloadTool.Contracts
{
    /// <summary>
    /// Declarative contract that a Worker can optionally implement to participate
    /// in state transfer across hot-reload restarts. Workers that do not implement
    /// this interface restart statelessly without error.
    /// </summary>
    /// <remarks>
    /// Implementors MUST be tolerant of <c>null</c> or empty JSON in <see cref="Initialize"/>
    /// — this represents a stateless or first-time start.
    /// <see cref="OnShutdown"/> MUST return a JSON-serializable snapshot of the
    /// worker's in-memory state. Non-serializable resources (connections, timers)
    /// MUST be reconstructed from serializable data in <see cref="Initialize"/>.
    /// </remarks>
    public interface IStatefulLifecycle
    {
        /// <summary>
        /// Called once on the new worker process after launch, before it begins
        /// accepting connections. Receives the JSON state snapshot captured from
        /// the previous worker, or <c>null</c> if no state was transferred.
        /// </summary>
        /// <param name="stateJson">
        /// JSON-serialized state from the previous worker's <see cref="OnShutdown"/>,
        /// or <c>null</c> for a stateless or first-time start.
        /// </param>
        void Initialize(string stateJson);

        /// <summary>
        /// Called on the old worker during the drain phase. Must return a
        /// JSON-serializable snapshot of the worker's current in-memory state.
        /// MUST complete quickly (within the drain timeout) — do not perform
        /// long-running I/O here.
        /// </summary>
        /// <returns>JSON string representing the worker's state.</returns>
        string OnShutdown();

        /// <summary>
        /// Signals the worker to stop accepting new connections and begin
        /// completing in-flight requests. After this call, the worker SHOULD
        /// refuse new HTTP requests and initiate WebSocket close handshakes.
        /// </summary>
        void BeginDrain();

        /// <summary>
        /// Polled by the Host to determine whether the worker has finished
        /// draining in-flight requests. Once this returns <c>true</c>, the
        /// Host may proceed to state transfer and termination.
        /// </summary>
        /// <returns>
        /// <c>true</c> if all in-flight requests have completed and the worker
        /// is ready for termination; <c>false</c> otherwise.
        /// </returns>
        bool IsDrainComplete();
    }
}
