using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace DonkeyWork.CodeSandbox.Server.IntegrationTests;

/// <summary>
/// Manages the lifecycle of a CodeExecutionServer container for integration tests
/// </summary>
public class ServerFixture : IAsyncLifetime
{
    private IContainer? _container;
    private const int ServerPort = 8666;

    /// <summary>
    /// The WebSocket URL to connect to the server
    /// </summary>
    public string ServerUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        const string imageName = "donkeywork-codesandbox-server:test";

        // Check if image already exists (e.g., pre-built in CI)
        if (!await ImageExistsAsync(imageName))
        {
            // Build the Docker image from the Dockerfile in the src/DonkeyWork.CodeSandbox.Server directory
            // Use custom solution directory finder that supports .slnx files
            var solutionDir = GetSolutionDirectory();
            var imageFuture = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(solutionDir, string.Empty)
                .WithDockerfile("src/DonkeyWork.CodeSandbox.Server/Dockerfile")
                .WithName(imageName)
                .WithCleanUp(true)
                .Build();

            await imageFuture.CreateAsync();
        }

        // Create and start the container
        _container = new ContainerBuilder()
            .WithImage(imageName)
            .WithPortBinding(ServerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(ServerPort))
            .Build();

        await _container.StartAsync();

        // Get the mapped port
        var mappedPort = _container.GetMappedPublicPort(ServerPort);
        ServerUrl = $"http://localhost:{mappedPort}";

        // Wait a bit for the server to be fully ready
        await Task.Delay(2000);

        // Verify the server is responding by attempting a simple connection check
        await WaitForServerHealthAsync();
    }

    private static async Task<bool> ImageExistsAsync(string imageName)
    {
        try
        {
            using var dockerClient = new Docker.DotNet.DockerClientConfiguration().CreateClient();
            var images = await dockerClient.Images.ListImagesAsync(new Docker.DotNet.Models.ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                }
            });
            return images.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForServerHealthAsync()
    {
        var maxRetries = 30;
        var retryDelay = TimeSpan.FromSeconds(1);

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // Try to connect via HTTP to check if the server is running
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);

                var response = await httpClient.GetAsync($"http://localhost:{_container!.GetMappedPublicPort(ServerPort)}/healthz");

                // We expect a 200 OK from the health endpoint
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
                // Server not ready yet
            }

            if (i < maxRetries - 1)
            {
                await Task.Delay(retryDelay);
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Finds the solution directory by looking for .sln or .slnx files
    /// Compatible with both legacy .sln and new .slnx solution formats
    /// </summary>
    private static CommonDirectoryPath GetSolutionDirectory()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory != null)
        {
            // Check for .sln or .slnx files
            if (directory.GetFiles("*.sln").Length > 0 ||
                directory.GetFiles("*.slnx").Length > 0)
            {
                return new CommonDirectoryPath(directory.FullName);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find solution directory (no .sln or .slnx file found)");
    }
}
