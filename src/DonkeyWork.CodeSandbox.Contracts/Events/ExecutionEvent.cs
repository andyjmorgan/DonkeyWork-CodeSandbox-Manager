using System.Text.Json.Serialization;

namespace DonkeyWork.CodeSandbox.Contracts.Events;

/// <summary>
/// Type of output stream
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputStreamType
{
    Stdout,
    Stderr
}

/// <summary>
/// Base class for all execution events streamed from the server
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(OutputEvent), typeDiscriminator: nameof(OutputEvent))]
[JsonDerivedType(typeof(CompletedEvent), typeDiscriminator: nameof(CompletedEvent))]
public abstract class ExecutionEvent
{
    public int Pid { get; set; }
}

/// <summary>
/// Event containing output data (stdout or stderr)
/// </summary>
public class OutputEvent : ExecutionEvent
{
    public OutputStreamType Stream { get; set; }
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Final event indicating process completion
/// </summary>
public class CompletedEvent : ExecutionEvent
{
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
}
