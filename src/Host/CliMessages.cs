namespace HotReloadTool.Host
{
    /// <summary>
    /// Commands that can be sent to a running Host via the CLI control pipe.
    /// </summary>
    public static class CliCommands
    {
        public const string Stop = "stop";
        public const string Status = "status";
    }

    /// <summary>
    /// Request message sent from a CLI control command to the running Host.
    /// </summary>
    public class CliRequestMessage
    {
        public string Command { get; set; }
    }

    /// <summary>
    /// Response message sent from the Host back to the CLI control command.
    /// </summary>
    public class CliResponseMessage
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string State { get; set; }
        public bool IsReloading { get; set; }
        public bool WorkerIsRunning { get; set; }
        public string ProjectPath { get; set; }
    }
}
