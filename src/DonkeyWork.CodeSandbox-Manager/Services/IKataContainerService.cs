using DonkeyWork.CodeSandbox_Manager.Models;

namespace DonkeyWork.CodeSandbox_Manager.Services;

public interface IKataContainerService
{
    Task<KataContainerInfo> CreateContainerAsync(CreateContainerRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ContainerCreationEvent> CreateContainerWithEventsAsync(CreateContainerRequest request, CancellationToken cancellationToken = default);
    Task<List<KataContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<KataContainerInfo?> GetContainerAsync(string podName, CancellationToken cancellationToken = default);
    Task<DeleteContainerResponse> DeleteContainerAsync(string podName, CancellationToken cancellationToken = default);

    // Execution passthrough methods
    IAsyncEnumerable<ExecutionEvent> ExecuteCommandAsync(string sandboxId, ExecutionRequest request, CancellationToken cancellationToken = default);
    Task<string> GetPodIpAsync(string sandboxId, CancellationToken cancellationToken = default);
    void UpdateLastActivity(string sandboxId);
    Task<DateTime?> GetLastActivityAsync(string sandboxId);
}
