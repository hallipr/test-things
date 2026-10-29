namespace DirectoryAccess.Functions;

public interface IRunDirectoryStore
{
    Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn,
        CancellationToken cancellationToken);
}

public sealed class DirectoryAccessBroker(
    IGitHubTokenValidator validator, IRunDirectoryStore store, TimeProvider clock)
{
    public async Task<DirectoryGrant> IssueAsync(string token, CancellationToken cancellationToken = default)
    {
        var identity = await validator.ValidateAsync(token, cancellationToken);
        var path = identity.DirectoryPath;
        if (!await store.IsDirectoryAsync(path, cancellationToken))
            throw new ForbiddenException();

        // SDK SAS timestamps serialize whole seconds. Truncate once BEFORE computing
        // both timestamps so the serialized interval is exactly 3,600 seconds.
        var startsOn = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        return await store.SignAsync(path, startsOn, startsOn.AddMinutes(60), cancellationToken);
    }
}
