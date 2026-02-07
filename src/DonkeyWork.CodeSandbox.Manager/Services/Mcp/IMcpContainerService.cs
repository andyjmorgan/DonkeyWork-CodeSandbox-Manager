using DonkeyWork.CodeSandbox.Manager.Models;

namespace DonkeyWork.CodeSandbox.Manager.Services.Mcp;

public interface IMcpContainerService
{
    IAsyncEnumerable<ContainerCreationEvent> CreateMcpServerAsync(
        CreateMcpServerRequest request,
        CancellationToken cancellationToken = default);

    Task<List<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken = default);

    Task<McpServerInfo?> GetMcpServerAsync(string podName, CancellationToken cancellationToken = default);

    Task<DeleteContainerResponse> DeleteMcpServerAsync(string podName, CancellationToken cancellationToken = default);

    Task<KataContainerInfo?> AllocateWarmMcpServerAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms an MCP server container by calling POST /api/mcp/start with the given launch command/args.
    /// Streams SSE events from the MCP server during startup.
    /// </summary>
    IAsyncEnumerable<McpStartProcessEvent> StartMcpProcessAsync(string podName, McpStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proxies a raw JSON-RPC request body to the MCP server inside the container.
    /// </summary>
    Task<string> ProxyMcpRequestAsync(string podName, string jsonRpcBody, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the MCP process status from inside the container.
    /// </summary>
    Task<McpStatusResponse> GetMcpStatusAsync(string podName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the MCP process inside the container (keeps pod alive for reuse).
    /// </summary>
    Task StopMcpProcessAsync(string podName, CancellationToken cancellationToken = default);

    Task<PoolStatistics> GetMcpPoolStatisticsAsync(CancellationToken cancellationToken = default);
}
