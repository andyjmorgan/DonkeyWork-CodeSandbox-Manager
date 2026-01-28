namespace DonkeyWork.CodeSandbox.McpServer.Models;

public class StartRequest
{
    public string[] PreExecScripts { get; set; } = [];
    public required string LaunchCommand { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
