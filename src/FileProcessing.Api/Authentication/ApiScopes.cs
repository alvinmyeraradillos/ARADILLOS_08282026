namespace FileProcessing.Api.Authentication;

/// <summary>
/// Scopes a key can carry. Keeping them as constants means a typo in a policy name is a compile
/// error rather than a silently unenforced endpoint.
/// </summary>
public static class ApiScopes
{
    /// <summary>Upload and process files.</summary>
    public const string FilesWrite = "files:write";

    /// <summary>Read the caller's own processed files.</summary>
    public const string FilesRead = "files:read";

    /// <summary>Read every client's processed files. Intended for an operations dashboard.</summary>
    public const string FilesReadAll = "files:read:all";

    /// <summary>Read the summary report.</summary>
    public const string ReportsRead = "reports:read";

    public static readonly string[] All = [FilesWrite, FilesRead, FilesReadAll, ReportsRead];

    /// <summary>Claim type used to carry a scope on the authenticated principal.</summary>
    public const string ClaimType = "scope";
}

/// <summary>Authorization policy names, one per protected capability.</summary>
public static class AuthorizationPolicies
{
    public const string UploadFiles = nameof(UploadFiles);
    public const string ReadFiles = nameof(ReadFiles);
    public const string ReadReports = nameof(ReadReports);
}
