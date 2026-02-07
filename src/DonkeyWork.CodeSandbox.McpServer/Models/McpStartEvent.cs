namespace DonkeyWork.CodeSandbox.McpServer.Models;

public class McpStartEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Stream { get; set; }
    public int? ExitCode { get; set; }
    public int? Pid { get; set; }
    public double? ElapsedSeconds { get; set; }
}
