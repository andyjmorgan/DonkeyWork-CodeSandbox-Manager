using DonkeyWork.CodeSandbox_Manager.Models;

namespace DonkeyWork.CodeSandbox_Manager.Services;

public interface IKataContainerService
{
    Task<KataContainerInfo> CreateContainerAsync(CreateContainerRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ContainerCreationEvent> CreateContainerWithEventsAsync(CreateContainerRequest request, CancellationToken cancellationToken = default);
    Task<List<KataContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<KataContainerInfo?> GetContainerAsync(string podName, CancellationToken cancellationToken = default);
    Task<DeleteContainerResponse> DeleteContainerAsync(string podName, CancellationToken cancellationToken = default);
}
