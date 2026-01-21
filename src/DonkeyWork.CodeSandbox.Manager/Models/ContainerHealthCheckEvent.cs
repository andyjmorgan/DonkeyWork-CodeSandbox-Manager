namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Event indicating the health check status of the Executor API in the container
/// </summary>
public class ContainerHealthCheckEvent : ContainerCreationEvent
{
    public ContainerHealthCheckEvent()
    {
        EventType = "healthcheck";
    }

    public bool IsHealthy { get; set; }
    public string? Message { get; set; }
    public string? PodIP { get; set; }
}
