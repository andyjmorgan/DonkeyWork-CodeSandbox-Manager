namespace DonkeyWork.CodeSandbox.Contracts.Requests;

/// <summary>
/// Request to execute a command
/// </summary>
public class ExecuteCommand
{
    public string Command { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 300;
}
