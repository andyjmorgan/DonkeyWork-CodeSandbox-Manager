namespace DonkeyWork.CodeSandbox_Manager.Models;

/// <summary>
/// Base class for all container creation events streamed from the server
/// </summary>
public abstract class ContainerCreationEvent
{
    public string PodName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
}
