namespace DonkeyWork.CodeSandbox.Manager.Models;

public class McpStatusResponse
{
    public string State { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastRequestAt { get; set; }
}
