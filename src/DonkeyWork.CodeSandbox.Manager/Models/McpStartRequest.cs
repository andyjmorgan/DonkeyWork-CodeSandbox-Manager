namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Request to start (arm) the MCP process inside an already-running container.
/// Mirrors the StartRequest model from the MCP server project.
/// </summary>
public class McpStartRequest
{
    public required string LaunchCommand { get; set; }
    public string[] PreExecScripts { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
}
