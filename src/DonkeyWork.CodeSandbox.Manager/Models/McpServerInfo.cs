namespace DonkeyWork.CodeSandbox.Manager.Models;

public class McpServerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastActivity { get; set; }
    public string? NodeName { get; set; }
    public string? PodIP { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public string? Image { get; set; }
    public string? LaunchCommand { get; set; }
    public McpProcessStatus McpStatus { get; set; } = McpProcessStatus.Unknown;
}

public enum McpProcessStatus
{
    Unknown,
    Idle,
    Initializing,
    Ready,
    Error,
    Disposed
}
