using FileProcessing.Api.Authentication;
using Microsoft.Extensions.Options;

namespace FileProcessing.UnitTests.Authentication;

public sealed class ApiKeyValidatorTests
{
    private const string GoodKey = "a-perfectly-good-key";

    [Fact]
    public void Accepts_a_configured_key_and_returns_its_client()
    {
        var result = Validate(GoodKey, Client("dummy-freight", GoodKey));

        Assert.Equal(ApiKeyStatus.Valid, result.Status);
        Assert.Equal("dummy-freight", result.Client?.ClientId);
    }

    [Fact]
    public void Rejects_an_unknown_key()
    {
        var result = Validate("not-the-key", Client("dummy-freight", GoodKey));

        Assert.Equal(ApiKeyStatus.Unknown, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public void Is_case_sensitive()
    {
        // The digest is over the exact bytes, so a case-folded key must not match. Worth pinning:
        // a case-insensitive comparison would shrink the key space enormously.
        var result = Validate(GoodKey.ToUpperInvariant(), Client("dummy-freight", GoodKey));

        Assert.Equal(ApiKeyStatus.Unknown, result.Status);
    }

    [Fact]
    public void Rejects_a_disabled_key()
    {
        var client = Client("dummy-freight", GoodKey);
        client.Enabled = false;

        Assert.Equal(ApiKeyStatus.Disabled, Validate(GoodKey, client).Status);
    }

    [Fact]
    public void Rejects_an_expired_key()
    {
        var client = Client("dummy-freight", GoodKey);
        client.ExpiresOnUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(ApiKeyStatus.Expired, Validate(GoodKey, client).Status);
    }

    [Fact]
    public void Accepts_a_key_whose_expiry_is_still_ahead()
    {
        var client = Client("dummy-freight", GoodKey);
        client.ExpiresOnUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(ApiKeyStatus.Valid, Validate(GoodKey, client).Status);
    }

    [Fact]
    public void Ignores_a_client_whose_configured_digest_is_malformed()
    {
        var broken = Client("broken", GoodKey);
        broken.KeySha256 = "not-a-digest";

        var result = Validate(GoodKey, broken);

        // A malformed entry must never be treated as a wildcard match.
        Assert.Equal(ApiKeyStatus.Unknown, result.Status);
    }

    [Fact]
    public void Picks_the_right_client_out_of_several()
    {
        var result = Validate(
            "second-key",
            Client("first", "first-key"),
            Client("second", "second-key"),
            Client("third", "third-key"));

        Assert.Equal("second", result.Client?.ClientId);
    }

    [Fact]
    public void Exposes_a_short_fingerprint_that_is_not_the_key()
    {
        var result = Validate(GoodKey, Client("dummy-freight", GoodKey));

        Assert.Equal(8, result.Fingerprint.Length);
        Assert.StartsWith(result.Fingerprint, ConfiguredApiKeyValidator.Sha256Hex(GoodKey), StringComparison.Ordinal);
        Assert.DoesNotContain(GoodKey, result.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_helper_matches_a_known_sha256()
    {
        // Guards the contract between the helper and any external tool an operator might use to
        // mint a digest (for example: printf '%s' key | sha256sum).
        Assert.Equal(
            "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb",
            ConfiguredApiKeyValidator.Sha256Hex("a"));
    }

    private static ApiKeyClient Client(string clientId, string key) => new()
    {
        ClientId = clientId,
        DisplayName = clientId,
        KeySha256 = ConfiguredApiKeyValidator.Sha256Hex(key),
        Scopes = [ApiScopes.FilesWrite],
        Enabled = true,
    };

    private static ApiKeyValidationResult Validate(string presented, params ApiKeyClient[] clients)
    {
        var options = new ApiKeyOptions { Clients = [.. clients] };
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var validator = new ConfiguredApiKeyValidator(
            new StaticOptionsMonitor<ApiKeyOptions>(options),
            timeProvider);

        return validator.Validate(presented);
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>A <see cref="TimeProvider"/> pinned to one instant, so expiry tests do not drift.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
