namespace DonkeyWork.CodeSandbox.Manager.Models;

public class McpServerStartingEvent : ContainerCreationEvent
{
    public McpServerStartingEvent() { EventType = "mcp_starting"; }
    public string Message { get; set; } = string.Empty;
}

public class McpServerStartedEvent : ContainerCreationEvent
{
    public McpServerStartedEvent() { EventType = "mcp_started"; }
    public McpServerInfo ServerInfo { get; set; } = null!;
    public double ElapsedSeconds { get; set; }
}

public class McpServerStartFailedEvent : ContainerCreationEvent
{
    public McpServerStartFailedEvent() { EventType = "mcp_start_failed"; }
    public string Reason { get; set; } = string.Empty;
}
