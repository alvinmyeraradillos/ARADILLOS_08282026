using System.ComponentModel.DataAnnotations;

namespace FileProcessing.Api.Authentication;

/// <summary>One API client and the scopes its key grants.</summary>
public sealed class ApiKeyClient
{
    /// <summary>Stable identifier for the calling system. Appears in logs and on every audit row.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string ClientId { get; set; } = string.Empty;

    [StringLength(128)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Lower-case hex SHA-256 of the API key. The plaintext key is never stored in configuration,
    /// so a leaked appsettings file or config store dump does not hand over working credentials.
    /// </summary>
    [Required]
    [RegularExpression("^[0-9a-fA-F]{64}$", ErrorMessage = "KeySha256 must be a 64 character hex SHA-256 digest.")]
    public string KeySha256 { get; set; } = string.Empty;

    /// <summary>Scopes granted to this key. See <see cref="ApiScopes"/>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Set to false to revoke a key without deleting its configuration.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional expiry. A key past this instant is rejected.</summary>
    public DateTimeOffset? ExpiresOnUtc { get; set; }
}

/// <summary>Bound from the <c>Authentication:ApiKey</c> configuration section.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Authentication:ApiKey";

    /// <summary>Request header carrying the key.</summary>
    [Required]
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>Value used in the <c>WWW-Authenticate</c> challenge.</summary>
    public string Realm { get; set; } = "file-processing";

    public List<ApiKeyClient> Clients { get; set; } = [];
}
