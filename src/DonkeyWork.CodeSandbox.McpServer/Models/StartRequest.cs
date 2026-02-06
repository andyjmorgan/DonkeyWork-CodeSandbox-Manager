namespace DonkeyWork.CodeSandbox.McpServer.Models;

public class StartRequest
{
    public string[] PreExecScripts { get; set; } = [];

    /// <summary>
    /// The command/executable to run (e.g., "npx", "node", "python")
    /// </summary>
    public required string Command { get; set; }

    /// <summary>
    /// Arguments to pass to the command
    /// </summary>
    public string[] Arguments { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 30;
}
