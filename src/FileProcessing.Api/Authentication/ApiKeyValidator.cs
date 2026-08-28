using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FileProcessing.Api.Authentication;

/// <summary>Why a presented key was accepted or turned away.</summary>
public enum ApiKeyStatus
{
    Valid,
    Unknown,
    Disabled,
    Expired,
}

/// <summary>Result of checking a presented key.</summary>
/// <param name="Status">Outcome of the check.</param>
/// <param name="Client">The matched client, or <see langword="null"/> when nothing matched.</param>
/// <param name="Fingerprint">
/// First eight hex characters of the presented key's digest. Safe to log — it identifies which key
/// was tried when diagnosing a misconfigured caller, without disclosing the key or being long
/// enough to be useful for reversing it.
/// </param>
public readonly record struct ApiKeyValidationResult(
    ApiKeyStatus Status,
    ApiKeyClient? Client,
    string Fingerprint);

public interface IApiKeyValidator
{
    ApiKeyValidationResult Validate(string presentedKey);
}

/// <summary>
/// Validates keys against the configured client list.
/// </summary>
/// <remarks>
/// Two properties matter here and both are deliberate:
/// <list type="bullet">
/// <item>Keys are compared as SHA-256 digests, so plaintext never appears in configuration.</item>
/// <item>The comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> and always
/// walks the whole client list. Returning early on the first match would leak, through response
/// timing, how much of a guessed key was correct and where in the list a client sits.</item>
/// </list>
/// A production deployment would move the client list into a store with per-key rotation and
/// last-used tracking; the interface is here so that swap does not touch the handler.
/// </remarks>
public sealed class ConfiguredApiKeyValidator(
    IOptionsMonitor<ApiKeyOptions> options,
    TimeProvider timeProvider) : IApiKeyValidator
{
    public ApiKeyValidationResult Validate(string presentedKey)
    {
        var digest = Sha256(presentedKey);
        var fingerprint = Convert.ToHexStringLower(digest)[..8];

        ApiKeyClient? matched = null;
        foreach (var client in options.CurrentValue.Clients)
        {
            if (!TryParseDigest(client.KeySha256, out var expected))
            {
                // Options validation rejects malformed digests at start-up; this is belt and braces
                // for a bad hot-reload, and must not be treated as a match.
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(digest, expected))
            {
                matched = client;
            }
        }

        if (matched is null)
        {
            return new ApiKeyValidationResult(ApiKeyStatus.Unknown, null, fingerprint);
        }

        if (!matched.Enabled)
        {
            return new ApiKeyValidationResult(ApiKeyStatus.Disabled, matched, fingerprint);
        }

        if (matched.ExpiresOnUtc is { } expiry && expiry <= timeProvider.GetUtcNow())
        {
            return new ApiKeyValidationResult(ApiKeyStatus.Expired, matched, fingerprint);
        }

        return new ApiKeyValidationResult(ApiKeyStatus.Valid, matched, fingerprint);
    }

    /// <summary>SHA-256 of the UTF-8 bytes of a key. Exposed so tooling and tests can derive digests.</summary>
    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>Convenience wrapper returning the lower-case hex digest of a key.</summary>
    public static string Sha256Hex(string value) => Convert.ToHexStringLower(Sha256(value));

    private static bool TryParseDigest(string hex, out byte[] digest)
    {
        digest = [];
        if (hex.Length != 64)
        {
            return false;
        }

        try
        {
            digest = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
