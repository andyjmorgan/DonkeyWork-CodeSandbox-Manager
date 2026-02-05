namespace DonkeyWork.CodeSandbox.Manager.Models;

public class CreateMcpServerRequest
{
    /// <summary>
    /// The command to launch the MCP stdio server inside the container.
    /// E.g., "npx -y @modelcontextprotocol/server-filesystem /home/user"
    /// </summary>
    public string? LaunchCommand { get; set; }

    /// <summary>
    /// Optional scripts to run before launching the MCP server.
    /// E.g., ["npm install -g some-package"]
    /// </summary>
    public string[] PreExecScripts { get; set; } = [];

    /// <summary>
    /// Timeout in seconds for the MCP server to start. Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public ResourceRequirements? Resources { get; set; }
}
