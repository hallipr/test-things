using Microsoft.Extensions.Logging;

namespace DirectoryAccess.Functions;

public interface IRunDirectoryStore
{
    Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<InvocationAuthorization?> GetAuthorizationAsync(string path, CancellationToken cancellationToken);
    Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn,
        CancellationToken cancellationToken);
}

public sealed class DirectoryAccessBroker(
    IGitHubTokenValidator validator, IRunDirectoryStore store, TimeProvider clock,
    ILogger<DirectoryAccessBroker> logger)
{
    public async Task<DirectoryGrant> IssueAsync(string token, string invocationId,
        CancellationToken cancellationToken = default)
    {
        var identity = await validator.ValidateAsync(token, cancellationToken);
        if (!long.TryParse(invocationId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0 ||
            number.ToString(System.Globalization.CultureInfo.InvariantCulture) != invocationId)
        {
            logger.LogWarning("Invocation ID rejected: expected a canonical positive Int64; no storage lookup performed");
            throw new ForbiddenException();
        }
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["InvocationId"] = invocationId, ["RepositoryId"] = identity.RepositoryId,
            ["RunId"] = identity.RunId, ["RunAttempt"] = identity.RunAttempt
        });
        logger.LogInformation("Invocation authorization started for {InvocationId}, repository {RepositoryId}, run {RunId}, attempt {RunAttempt}",
            invocationId, identity.RepositoryId, identity.RunId, identity.RunAttempt);
        if (!await store.IsDirectoryAsync(invocationId, cancellationToken))
        {
            logger.LogWarning("Invocation authorization denied: directory missing or path is a file");
            throw new ForbiddenException();
        }
        var authorization = await store.GetAuthorizationAsync(invocationId, cancellationToken);
        if (authorization is null)
        {
            logger.LogWarning("Invocation authorization denied: operator-written authorization blob is missing");
            throw new ForbiddenException();
        }
        var invocationMatches = authorization.InvocationId == invocationId;
        var repositoryMatches = authorization.Run.RepositoryId == identity.RepositoryId;
        var runMatches = authorization.Run.RunId == identity.RunId;
        var attemptMatches = authorization.Run.RunAttempt == identity.RunAttempt;
        logger.LogInformation("Invocation mapping vetting: invocation match {InvocationMatches}, repository match {RepositoryMatches}, run match {RunMatches}, attempt match {AttemptMatches}",
            invocationMatches, repositoryMatches, runMatches, attemptMatches);
        if (!invocationMatches || !repositoryMatches || !runMatches || !attemptMatches)
        {
            logger.LogWarning("Invocation authorization denied: trusted mapping does not match the signed run identity; no SAS issued");
            throw new ForbiddenException();
        }
        logger.LogInformation("Invocation authorization approved; requesting a 60-minute, HTTPS-only directory read/list SAS");

        // SDK SAS timestamps serialize whole seconds. Truncate once BEFORE computing
        // both timestamps so the serialized interval is exactly 3,600 seconds.
        var startsOn = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        return await store.SignAsync(invocationId, startsOn, startsOn.AddMinutes(60), cancellationToken);
    }
}
