namespace DonkeyWork.CodeSandbox.Manager.Models;

public class DeleteContainerResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? PodName { get; set; }
}
