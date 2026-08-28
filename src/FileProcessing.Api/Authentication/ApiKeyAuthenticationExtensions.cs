using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FileProcessing.Api.Authentication;

/// <summary>
/// Fails the host at start-up on a bad key configuration. A service that boots with an unusable or
/// dangerously loose key list is worse than one that refuses to start, because the problem only
/// surfaces on the first real request.
/// </summary>
public sealed class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.HeaderName))
        {
            failures.Add("Authentication:ApiKey:HeaderName must be set.");
        }

        if (options.Clients.Count == 0)
        {
            failures.Add(
                "Authentication:ApiKey:Clients contains no clients, so no caller could ever authenticate.");
        }

        var seenClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDigests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Clients.Count; i++)
        {
            var client = options.Clients[i];
            var prefix = $"Authentication:ApiKey:Clients[{i}]";

            if (string.IsNullOrWhiteSpace(client.ClientId))
            {
                failures.Add($"{prefix}:ClientId must be set.");
            }
            else if (!seenClientIds.Add(client.ClientId))
            {
                failures.Add($"{prefix}:ClientId '{client.ClientId}' is used more than once.");
            }

            if (client.KeySha256.Length != 64 || !client.KeySha256.All(Uri.IsHexDigit))
            {
                failures.Add($"{prefix}:KeySha256 must be a 64 character hex SHA-256 digest.");
            }
            else if (!seenDigests.Add(client.KeySha256))
            {
                failures.Add($"{prefix}:KeySha256 is shared with another client; keys must be unique.");
            }

            if (client.Scopes.Length == 0)
            {
                failures.Add($"{prefix}:Scopes is empty, so the key would authenticate but authorise nothing.");
            }

            var unknown = client.Scopes.Except(ApiScopes.All, StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0)
            {
                failures.Add($"{prefix}:Scopes contains unknown values: {string.Join(", ", unknown)}.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Wires up the API key scheme, its configuration validation and the scope-based policies.
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection(ApiKeyOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ApiKeyOptions>, ApiKeyOptionsValidator>();
        services.TryAddSingletonTimeProvider();
        services.AddSingleton<IApiKeyValidator, ConfiguredApiKeyValidator>();

        services
            .AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
            .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationDefaults.Scheme,
                displayName: "API key",
                configureOptions: null);

        services.AddAuthorizationBuilder()
            // Nothing is reachable without a valid key unless it opts out with [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(ApiKeyAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(
                AuthorizationPolicies.UploadFiles,
                policy => policy.RequireClaim(ApiScopes.ClaimType, ApiScopes.FilesWrite))
            .AddPolicy(
                AuthorizationPolicies.ReadFiles,
                policy => policy.RequireClaim(ApiScopes.ClaimType, ApiScopes.FilesRead, ApiScopes.FilesReadAll))
            .AddPolicy(
                AuthorizationPolicies.ReadReports,
                policy => policy.RequireClaim(ApiScopes.ClaimType, ApiScopes.ReportsRead));

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
