namespace HotReloadTool.Contracts
{
    /// <summary>
    /// Lightweight hook interface for components that need to observe lifecycle
    /// transitions without participating in state transfer. Implementors of
    /// <see cref="IStatefulLifecycle"/> implicitly satisfy this contract.
    /// </summary>
    public interface ILifecycleHook
    {
        /// <summary>
        /// Called when the Host is about to initiate a reload cycle.
        /// Components can use this to pause background work or flush buffers.
        /// </summary>
        void OnReloadStarting();

        /// <summary>
        /// Called after a reload cycle completes (success or failure).
        /// <paramref name="success"/> indicates whether the new worker launched.
        /// </summary>
        /// <param name="success">
        /// <c>true</c> if the reload cycle completed successfully; <c>false</c>
        /// if the build failed or the new worker could not start.
        /// </param>
        void OnReloadCompleted(bool success);
    }
}
