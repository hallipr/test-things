namespace DirectoryAccess.Functions;

public interface IRunDirectoryStore
{
    Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<InvocationAuthorization?> GetAuthorizationAsync(string path, CancellationToken cancellationToken);
    Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn,
        CancellationToken cancellationToken);
}

public sealed class DirectoryAccessBroker(
    IGitHubTokenValidator validator, IRunDirectoryStore store, TimeProvider clock)
{
    public async Task<DirectoryGrant> IssueAsync(string token, string invocationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await validator.ValidateAsync(token, cancellationToken);
        if (!long.TryParse(invocationId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0 ||
            number.ToString(System.Globalization.CultureInfo.InvariantCulture) != invocationId)
            throw new ForbiddenException();
        if (!await store.IsDirectoryAsync(invocationId, cancellationToken))
            throw new ForbiddenException();
        var authorization = await store.GetAuthorizationAsync(invocationId, cancellationToken);
        if (authorization is null || authorization.InvocationId != invocationId || authorization.Run != identity)
            throw new ForbiddenException();

        // SDK SAS timestamps serialize whole seconds. Truncate once BEFORE computing
        // both timestamps so the serialized interval is exactly 3,600 seconds.
        var startsOn = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        return await store.SignAsync(invocationId, startsOn, startsOn.AddMinutes(60), cancellationToken);
    }
}
