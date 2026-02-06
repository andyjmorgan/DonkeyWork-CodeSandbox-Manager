namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Request to start (arm) the MCP process inside an already-running container.
/// Mirrors the StartRequest model from the MCP server project.
/// </summary>
public class McpStartRequest
{
    /// <summary>
    /// The command/executable to run (e.g., "npx", "node", "python").
    /// </summary>
    public required string Command { get; set; }

    /// <summary>
    /// Arguments to pass to the command.
    /// </summary>
    public string[] Arguments { get; set; } = [];

    public string[] PreExecScripts { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
}
