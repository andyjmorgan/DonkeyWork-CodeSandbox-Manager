namespace DonkeyWork.CodeSandbox.Manager.Models;

/// <summary>
/// Event streamed from the MCP server's /api/mcp/start SSE endpoint.
/// Mirrors the McpStartEvent model from the MCP Server project.
/// </summary>
public class McpStartProcessEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Stream { get; set; }
    public int? ExitCode { get; set; }
    public int? Pid { get; set; }
    public double? ElapsedSeconds { get; set; }
}
