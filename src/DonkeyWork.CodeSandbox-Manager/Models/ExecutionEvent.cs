namespace DonkeyWork.CodeSandbox_Manager.Models;

/// <summary>
/// Base class for execution events from CodeExecution API
/// </summary>
public abstract class ExecutionEvent
{
    public int Pid { get; set; }
}

/// <summary>
/// Output event from stdout or stderr
/// </summary>
public class OutputEvent : ExecutionEvent
{
    public string Stream { get; set; } = string.Empty; // "Stdout" or "Stderr"
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Completion event when execution finishes
/// </summary>
public class CompletedEvent : ExecutionEvent
{
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
}
