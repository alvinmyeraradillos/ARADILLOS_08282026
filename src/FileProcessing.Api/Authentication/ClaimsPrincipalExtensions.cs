using System.Security.Claims;

namespace FileProcessing.Api.Authentication;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The authenticated client's id. Endpoints run behind a fallback policy that requires
    /// authentication, so reaching one without this claim means the pipeline was misconfigured —
    /// hence a throw rather than a silent empty string that would end up on an audit row.
    /// </summary>
    public static string GetClientId(this ClaimsPrincipal principal)
    {
        var clientId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(clientId)
            ? throw new InvalidOperationException("The authenticated principal carries no client id claim.")
            : clientId;
    }

    public static bool CanReadAllClients(this ClaimsPrincipal principal) =>
        principal.HasClaim(ApiScopes.ClaimType, ApiScopes.FilesReadAll);

    /// <summary>
    /// The client id that read queries must be restricted to, or <see langword="null"/> when the
    /// caller holds the cross-client scope. Callers pass this straight to the repository, so
    /// isolation is applied even if an endpoint forgets to think about it.
    /// </summary>
    public static string? GetReadRestriction(this ClaimsPrincipal principal) =>
        principal.CanReadAllClients() ? null : principal.GetClientId();
}
