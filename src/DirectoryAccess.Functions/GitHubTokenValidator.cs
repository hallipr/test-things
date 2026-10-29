using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DirectoryAccess.Functions;

public interface IGitHubTokenValidator
{
    Task<RunIdentity> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed class GitHubTokenValidator(
    BrokerOptions options,
    IConfigurationManager<OpenIdConnectConfiguration> configuration) : IGitHubTokenValidator
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
            throw new InvalidTokenException();

        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            // Never authorize without successfully obtained trusted signing metadata.
            throw new UpstreamException();
        }
        if (!string.Equals(metadata.Issuer, BrokerOptions.Issuer, StringComparison.Ordinal) ||
            metadata.SigningKeys.Count == 0)
            throw new UpstreamException();

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
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // ConfigurationManager caches metadata and rate-limits refresh. A rotated key
            // triggers refresh for a subsequent request; this request always fails closed.
            configuration.RequestRefresh();
            throw new InvalidTokenException();
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
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
                throw new ForbiddenException();
            return values[0].Value.GetString()!;
        }
        
        // Also disallow duplicate/array issuer and audience representations.
        if (SingleString("iss") != BrokerOptions.Issuer || SingleString("aud") != options.Audience)
            throw new InvalidTokenException();

        var repository = SingleString("repository");
        var repositoryId = SingleString("repository_id");
        var runId = SingleString("run_id");
        var attempt = SingleString("run_attempt");

        if (repository != BrokerOptions.Repository || repositoryId != BrokerOptions.RepositoryId ||
            !IsCanonicalPositiveDecimal(runId) || !IsCanonicalPositiveDecimal(attempt))
            throw new ForbiddenException();

        return new RunIdentity(repositoryId, runId, attempt);
    }

    private static bool IsCanonicalPositiveDecimal(string value) =>
        value.Length is > 0 and <= 20 && value[0] is >= '1' and <= '9' &&
        value.All(c => c is >= '0' and <= '9');
}
