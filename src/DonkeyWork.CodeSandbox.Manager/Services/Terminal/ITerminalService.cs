using System.Net.WebSockets;

namespace DonkeyWork.CodeSandbox.Manager.Services.Terminal;

/// <summary>
/// Service for managing WebSocket terminal sessions to Kubernetes pods via exec.
/// </summary>
public interface ITerminalService
{
    /// <summary>
    /// Handles a WebSocket terminal session for the specified sandbox.
    /// Bridges the browser WebSocket to Kubernetes exec streaming.
    /// </summary>
    /// <param name="sandboxId">The pod name (sandbox ID) to connect to</param>
    /// <param name="webSocket">The browser WebSocket connection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task HandleTerminalSessionAsync(
        string sandboxId,
        WebSocket webSocket,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resizes the terminal for the specified session.
    /// </summary>
    /// <param name="sandboxId">The sandbox ID</param>
    /// <param name="cols">Number of columns</param>
    /// <param name="rows">Number of rows</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ResizeTerminalAsync(
        string sandboxId,
        int cols,
        int rows,
        CancellationToken cancellationToken = default);
}
