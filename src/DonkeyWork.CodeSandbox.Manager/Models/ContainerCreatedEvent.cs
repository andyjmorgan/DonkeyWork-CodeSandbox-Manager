namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Event indicating the pod has been created in Kubernetes
/// </summary>
public class ContainerCreatedEvent : ContainerCreationEvent
{
    public ContainerCreatedEvent()
    {
        EventType = "created";
    }

    public string Phase { get; set; } = string.Empty;
}
