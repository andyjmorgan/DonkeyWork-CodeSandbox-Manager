namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Event indicating we're waiting for the container to be ready
/// </summary>
public class ContainerWaitingEvent : ContainerCreationEvent
{
    public ContainerWaitingEvent()
    {
        EventType = "waiting";
    }

    public int AttemptNumber { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
