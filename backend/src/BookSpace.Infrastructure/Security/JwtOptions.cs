using System.ComponentModel.DataAnnotations;

namespace BookSpace.Infrastructure.Security;

// Bound from the "Jwt" configuration section and validated at startup
// (ValidateOnStart in DependencyInjection), so a missing or weak signing key
// fails the boot instead of silently producing forgeable tokens.
// SigningKey is never in appsettings.json — user-secrets in development,
// the Jwt__SigningKey environment variable elsewhere (CLAUDE.md §4.4).
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    // 256 bits, matching HMAC-SHA256. Shorter keys are rejected rather than
    // zero-padded, which is what the raw crypto primitive would otherwise do.
    [Required(AllowEmptyStrings = false)]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters (256 bits).")]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;
}
