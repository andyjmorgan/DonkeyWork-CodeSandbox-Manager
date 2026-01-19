namespace DonkeyWork.CodeSandbox_Manager.Models;

/// <summary>
/// Event indicating the container creation failed or timed out
/// </summary>
public class ContainerFailedEvent : ContainerCreationEvent
{
    public ContainerFailedEvent()
    {
        EventType = "failed";
    }

    public string Reason { get; set; } = string.Empty;
    public KataContainerInfo? ContainerInfo { get; set; }
}
