namespace DonkeyWork.CodeSandbox.Manager.Models;

public class DeleteContainerResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? PodName { get; set; }
}

public class DeleteAllContainersResponse
{
    public int DeletedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> DeletedPods { get; set; } = new();
    public List<string> FailedPods { get; set; } = new();
}
