namespace DonkeyWork.CodeSandbox.McpServer.Models;

public enum McpServerState
{
    Idle,
    Initializing,
    Ready,
    Error,
    Disposed
}

public class McpServerStatusResponse
{
    public McpServerState State { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastRequestAt { get; set; }
}
