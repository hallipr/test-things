namespace DirectoryAccess.Functions;

public sealed record BrokerOptions
{
    public const string Issuer = "https://token.actions.githubusercontent.com";
    public const string Repository = "hallipr/test-things";
    public const string RepositoryId = "1437763204";
    public const string FileSystem = "runs";
    public const string AuthorizationFile = ".authorization.json";
    public required string Audience { get; init; }
    public required Uri StorageUrl { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("GitHubOidcAudience must be configured.");
        if (!StorageUrl.IsAbsoluteUri || StorageUrl.Scheme != "https" ||
            !StorageUrl.Host.EndsWith(".dfs.core.windows.net", StringComparison.Ordinal) ||
            StorageUrl.AbsolutePath != "/" || !string.IsNullOrEmpty(StorageUrl.Query) ||
            !string.IsNullOrEmpty(StorageUrl.Fragment) || !string.IsNullOrEmpty(StorageUrl.UserInfo) ||
            !StorageUrl.IsDefaultPort)
            throw new InvalidOperationException("RunStorageAccountUrl must be an Azure public-cloud DFS account HTTPS endpoint.");
    }
}

public sealed class InvalidTokenException : Exception;
public sealed class ForbiddenException : Exception;
public sealed class UpstreamException : Exception;

public sealed record RunIdentity(string RepositoryId, string RunId, string RunAttempt);

public sealed record InvocationAuthorization(string InvocationId, RunIdentity Run);

// Keep credentials out of generated record ToString() output.
public sealed class DirectoryGrant
{
    public required string DirectoryUrl { get; init; }
    public required string SasToken { get; init; }
    public required DateTimeOffset StartsOn { get; init; }
    public required DateTimeOffset ExpiresOn { get; init; }
    public override string ToString() => nameof(DirectoryGrant);
}