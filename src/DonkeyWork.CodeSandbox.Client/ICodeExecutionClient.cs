namespace DonkeyWork.CodeSandbox.Client;

using DonkeyWork.CodeSandbox.Contracts.Events;

/// <summary>
/// Client interface for executing commands on a remote code execution server
/// </summary>
public interface ICodeExecutionClient : IDisposable
{
    /// <summary>
    /// Gets whether the client is currently connected to the server
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connects to the code execution server
    /// </summary>
    Task ConnectAsync(string url, Guid userId, Guid executionId, Guid conversationId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the server
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command and streams all events (output and completion) as they arrive
    /// </summary>
    IAsyncEnumerable<ExecutionEvent> ExecuteAsync(string command, int timeoutSeconds = 300, CancellationToken cancellationToken = default);
}
