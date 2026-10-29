using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;

namespace DirectoryAccess.Functions;

public interface IGitHubTokenValidator
{
    Task<RunIdentity> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed class GitHubTokenValidator(
    BrokerOptions options,
    IConfigurationManager<OpenIdConnectConfiguration> configuration,
    ILogger<GitHubTokenValidator> logger) : IGitHubTokenValidator
{
    public static IConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{BrokerOptions.Issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true })
        {
            UseLastKnownGoodConfiguration = false
        };

    public async Task<RunIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384)
        {
            logger.LogWarning("OIDC token rejected: missing or exceeds the 16384-character limit");
            throw new InvalidTokenException();
        }

        logger.LogInformation("OIDC validation started; expected issuer {Issuer}, audience {Audience}, algorithm RS256; loading trusted discovery metadata",
            BrokerOptions.Issuer, options.Audience);
        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            logger.LogError("OIDC discovery failed; failure type {FailureType}; no token authorized", ex.GetType().Name);
            // Never authorize without successfully obtained trusted signing metadata.
            throw new UpstreamException();
        }
        if (!string.Equals(metadata.Issuer, BrokerOptions.Issuer, StringComparison.Ordinal) ||
            metadata.SigningKeys.Count == 0)
        {
            logger.LogError("OIDC discovery rejected; issuer match {IssuerMatches}, signing key count {SigningKeyCount}",
                metadata.Issuer == BrokerOptions.Issuer, metadata.SigningKeys.Count);
            throw new UpstreamException();
        }
        logger.LogInformation("OIDC discovery ready; issuer verified, signing key count {SigningKeyCount}", metadata.SigningKeys.Count);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = BrokerOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        };
        JwtSecurityToken jwt;
        try
        {
            handler.ValidateToken(token, parameters, out var validated);
            jwt = (JwtSecurityToken)validated;
            logger.LogInformation("OIDC signature, algorithm, issuer, audience and lifetime checks passed; algorithm {Algorithm}, key ID {KeyId}, expires UTC {TokenExpiresUtc}",
                jwt.Header.Alg, jwt.Header.Kid, jwt.ValidTo);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // ConfigurationManager caches metadata and rate-limits refresh. A rotated key
            // triggers refresh for a subsequent request; this request always fails closed.
            configuration.RequestRefresh();
            logger.LogWarning("OIDC signing key not found; discovery refresh requested; this request remains denied");
            throw new InvalidTokenException();
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger.LogWarning("OIDC cryptographic or standard claim validation rejected the token; reason {ValidationFailure}", ex.GetType().Name);
            throw new InvalidTokenException();
        }

        // Inspect the SIGNED raw payload: JwtSecurityToken/ClaimsPrincipal can flatten
        // arrays or overwrite duplicate JSON properties. Neither is unambiguous authority.
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.RawPayload));
        var root = payload.RootElement;
        string SingleString(string name)
        {
            var values = root.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
            if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String)
            {
                logger.LogWarning("OIDC claim rejected: {ClaimName} must occur exactly once as a string; occurrences {ClaimCount}", name, values.Length);
                throw new ForbiddenException();
            }
            return values[0].Value.GetString()!;
        }
        
        // Also disallow duplicate/array issuer and audience representations.
        if (SingleString("iss") != BrokerOptions.Issuer || SingleString("aud") != options.Audience)
        {
            logger.LogWarning("OIDC raw issuer or audience representation did not match the configured value");
            throw new InvalidTokenException();
        }

        var repository = SingleString("repository");
        var repositoryId = SingleString("repository_id");
        var runId = SingleString("run_id");
        var attempt = SingleString("run_attempt");

        var repositoryMatches = repository == BrokerOptions.Repository;
        var repositoryIdMatches = repositoryId == BrokerOptions.RepositoryId;
        var runIdCanonical = IsCanonicalPositiveDecimal(runId);
        var attemptCanonical = IsCanonicalPositiveDecimal(attempt);
        logger.LogInformation("OIDC claim vetting: repository match {RepositoryMatches}, immutable repository ID match {RepositoryIdMatches}, canonical run ID {RunIdCanonical}, canonical attempt {AttemptCanonical}",
            repositoryMatches, repositoryIdMatches, runIdCanonical, attemptCanonical);
        if (!repositoryMatches || !repositoryIdMatches || !runIdCanonical || !attemptCanonical)
        {
            logger.LogWarning("OIDC repository or run claims rejected; no storage authorization attempted");
            throw new ForbiddenException();
        }

        logger.LogInformation("OIDC claims accepted: repository {Repository}, repository ID {RepositoryId}, run ID {RunId}, run attempt {RunAttempt}",
            repository, repositoryId, runId, attempt);
        return new RunIdentity(repositoryId, runId, attempt);
    }

    private static bool IsCanonicalPositiveDecimal(string value) =>
        value.Length is > 0 and <= 20 && value[0] is >= '1' and <= '9' &&
        value.All(c => c is >= '0' and <= '9');
}
