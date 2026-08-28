using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FileProcessing.Api.Authentication;

public static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "ApiKey";
}

/// <summary>Scheme options. Nothing extra today, but it keeps the scheme extensible.</summary>
public sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authentication middleware for the API key scheme.
/// </summary>
/// <remarks>
/// The check runs inside ASP.NET Core's authentication middleware rather than as a bare
/// <c>app.Use(...)</c> delegate. That is a deliberate choice: a hand-rolled delegate has to
/// re-implement scheme selection, the 401 challenge, the 403 forbid path and the plumbing that
/// makes <c>[Authorize]</c>, <c>[AllowAnonymous]</c> and policy-based scopes work. Implementing
/// <see cref="AuthenticationHandler{TOptions}"/> gets all of that from the framework, and means
/// endpoints opt in with an attribute instead of the middleware carrying a path allow-list that
/// someone will eventually forget to update.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptionsMonitor<ApiKeyOptions> apiKeyOptions,
    IApiKeyValidator validator)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    private const int MaxKeyLength = 512;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerName = apiKeyOptions.CurrentValue.HeaderName;

        if (!Request.Headers.TryGetValue(headerName, out var values))
        {
            // NoResult rather than Fail: no credential was offered, so the challenge should ask
            // for one instead of reporting a bad one.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1)
        {
            Logger.LogWarning("Rejected a request presenting {Count} {Header} headers.", values.Count, headerName);
            return Task.FromResult(AuthenticateResult.Fail("Exactly one API key header must be supplied."));
        }

        var presented = values[0];
        if (string.IsNullOrWhiteSpace(presented))
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key header is empty."));
        }

        if (presented.Length > MaxKeyLength)
        {
            // Nothing legitimate is this long; refuse before spending time hashing it.
            Logger.LogWarning("Rejected an over-long API key of {Length} characters.", presented.Length);
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var result = validator.Validate(presented);

        switch (result.Status)
        {
            case ApiKeyStatus.Valid when result.Client is { } client:
                Logger.LogDebug("Authenticated client {ClientId}.", client.ClientId);
                return Task.FromResult(AuthenticateResult.Success(CreateTicket(client)));

            case ApiKeyStatus.Disabled:
                Logger.LogWarning(
                    "Rejected a disabled API key {Fingerprint} for client {ClientId}.",
                    result.Fingerprint,
                    result.Client?.ClientId);
                break;

            case ApiKeyStatus.Expired:
                Logger.LogWarning(
                    "Rejected an expired API key {Fingerprint} for client {ClientId}.",
                    result.Fingerprint,
                    result.Client?.ClientId);
                break;

            default:
                Logger.LogWarning(
                    "Rejected an unrecognised API key {Fingerprint} from {RemoteIp}.",
                    result.Fingerprint,
                    Context.Connection.RemoteIpAddress);
                break;
        }

        // Every failure returns the same opaque message. Telling a caller whether a key is unknown,
        // disabled or merely expired confirms which guesses were real keys.
        return Task.FromResult(AuthenticateResult.Fail("The supplied API key is not valid."));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate =
            $"{ApiKeyAuthenticationDefaults.Scheme} realm=\"{apiKeyOptions.CurrentValue.Realm}\", " +
            $"header=\"{apiKeyOptions.CurrentValue.HeaderName}\"";

        await WriteProblemAsync(
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            $"Supply a valid API key in the {apiKeyOptions.CurrentValue.HeaderName} header.");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await WriteProblemAsync(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            "This API key is valid but does not carry the scope required for this endpoint.");
    }

    private AuthenticationTicket CreateTicket(ApiKeyClient client)
    {
        var claims = new List<Claim>(client.Scopes.Length + 2)
        {
            new(ClaimTypes.NameIdentifier, client.ClientId),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(client.DisplayName) ? client.ClientId : client.DisplayName),
        };

        foreach (var scope in client.Scopes)
        {
            claims.Add(new Claim(ApiScopes.ClaimType, scope));
        }

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.Scheme, ClaimTypes.Name, roleType: null);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
    }

    private async Task WriteProblemAsync(int statusCode, string title, string detail)
    {
        var problemDetailsService = Context.RequestServices.GetService<IProblemDetailsService>();
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = Request.GetEncodedPathAndQuery(),
        };

        if (problemDetailsService is null
            || !await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = Context,
                ProblemDetails = problem,
            }))
        {
            Response.ContentType = "application/problem+json";
            await Response.WriteAsJsonAsync(problem, Context.RequestAborted);
        }
    }
}
