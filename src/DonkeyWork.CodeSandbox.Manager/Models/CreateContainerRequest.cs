namespace DonkeyWork.CodeSandbox.Manager.Models;

public class CreateContainerRequest
{
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public List<string>? Command { get; set; }
    public List<string>? Args { get; set; }
    public ResourceRequirements? Resources { get; set; }

    /// <summary>
    /// If true, waits for the container to be ready before returning.
    /// If false, returns immediately after creating the pod (default).
    /// Kata containers typically take 12-25 seconds to start.
    /// </summary>
    public bool WaitForReady { get; set; } = false;
}

public class ResourceRequirements
{
    public ResourceSpec? Requests { get; set; }
    public ResourceSpec? Limits { get; set; }
}

public class ResourceSpec
{
    public int? MemoryMi { get; set; }
    public int? CpuMillicores { get; set; }
}
