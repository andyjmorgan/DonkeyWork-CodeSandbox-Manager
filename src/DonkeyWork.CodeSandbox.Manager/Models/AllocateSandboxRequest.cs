using System.ComponentModel.DataAnnotations;

namespace DonkeyWork.CodeSandbox.Manager.Models;

public class AllocateSandboxRequest
{
    [Required]
    [MinLength(1)]
    public string UserId { get; set; } = string.Empty;
}
