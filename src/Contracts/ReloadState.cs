namespace HotReloadTool.Contracts
{
    /// <summary>
    /// Represents the current phase of the reload orchestration state machine.
    /// </summary>
    public enum ReloadState
    {
        /// <summary>Idle — monitoring for file changes, no reload in progress.</summary>
        Idle = 0,

        /// <summary>File change detected; debounce timer running.</summary>
        Debouncing,

        /// <summary>MSBuild compilation in progress.</summary>
        Building,

        /// <summary>Build succeeded; copying build output to shadow directory.</summary>
        ShadowCopying,

        /// <summary>Draining connections on old worker.</summary>
        Draining,

        /// <summary>Transferring state via Named Pipes.</summary>
        TransferringState,

        /// <summary>Terminating old worker process.</summary>
        TerminatingOld,

        /// <summary>Launching new worker from shadow copy.</summary>
        LaunchingNew,

        /// <summary>Copying build output into the web site Bin folder (build-recycle mode).</summary>
        Deploying,

        /// <summary>Touching the recycle target so ASP.NET recycles the AppDomain (build-recycle mode).</summary>
        Recycling,

        /// <summary>Reload cycle failed (build error, timeout, etc.).</summary>
        Failed
    }
}
