using System.ComponentModel.DataAnnotations;

namespace SonosControl.Web.Models;

public sealed class UserFavouriteSource
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string SourceType { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    public string SourceUrl { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ApplicationUser User { get; set; } = null!;
}
