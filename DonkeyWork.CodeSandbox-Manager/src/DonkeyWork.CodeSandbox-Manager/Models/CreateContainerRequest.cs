namespace DonkeyWork.CodeSandbox_Manager.Models;

public class CreateContainerRequest
{
    public string Image { get; set; } = string.Empty;
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public List<string>? Command { get; set; }
    public List<string>? Args { get; set; }
    public ResourceRequirements? Resources { get; set; }
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
