namespace DonkeyWork.CodeSandbox_Manager.Models;

/// <summary>
/// Event indicating the container is ready
/// </summary>
public class ContainerReadyEvent : ContainerCreationEvent
{
    public ContainerReadyEvent()
    {
        EventType = "ready";
    }

    public KataContainerInfo ContainerInfo { get; set; } = null!;
    public double ElapsedSeconds { get; set; }
}
