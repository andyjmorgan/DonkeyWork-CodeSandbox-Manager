namespace DonkeyWork.CodeSandbox_Manager.Models;

/// <summary>
/// Request to execute a command in a sandbox container
/// </summary>
public class ExecutionRequest
{
    /// <summary>
    /// The bash command to execute
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Maximum execution time in seconds (default: 300)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
