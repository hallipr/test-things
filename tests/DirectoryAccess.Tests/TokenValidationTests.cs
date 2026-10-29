using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirectoryAccess.Functions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DirectoryAccess.Tests;

public sealed class TokenValidationTests : IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private const string Audience = "api://test";
    private static BrokerOptions Options => new() { Audience = Audience, StorageUrl = new("https://test.dfs.core.windows.net") };
    private Dictionary<string, object> Claims() => new()
    {
        ["iss"] = BrokerOptions.Issuer, ["aud"] = Audience,
        ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        ["repository"] = BrokerOptions.Repository, ["repository_id"] = BrokerOptions.RepositoryId,
        ["run_id"] = "123456789", ["run_attempt"] = "2"
    };

    private string Sign(string payload, RSA? signer = null, string algorithm = "RS256")
    {
        var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = algorithm, kid = "test-key", typ = "JWT" }));
        var data = header + "." + Base64UrlEncoder.Encode(payload);
        return data + "." + Base64UrlEncoder.Encode((signer ?? rsa).SignData(
            Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private GitHubTokenValidator Validator(TestConfiguration? config = null) =>
        new(Options, config ?? new TestConfiguration(new OpenIdConnectConfiguration
        {
            Issuer = BrokerOptions.Issuer,
            SigningKeys = { new RsaSecurityKey(rsa) { KeyId = "test-key" } }
        }));

    [Fact]
    public async Task Valid_signed_identity_has_exact_run_claims()
    {
        var identity = await Validator().ValidateAsync(Sign(JsonSerializer.Serialize(Claims())), default);
        Assert.Equal(new RunIdentity("1437763204", "123456789", "2"), identity);
    }

    [Theory]
    [InlineData("repository", "other/test-things")]
    [InlineData("repository", "Hallipr/test-things")]
    [InlineData("repository_id", "1437763205")]
    [InlineData("repository_id", "01437763204")]
    [InlineData("run_id", "")]
    [InlineData("run_id", "0")]
    [InlineData("run_id", "01")]
    [InlineData("run_id", "-1")]
    [InlineData("run_id", "+1")]
    [InlineData("run_id", "../1")]
    [InlineData("run_id", "1/2")]
    [InlineData("run_id", "1e2")]
    [InlineData("run_id", "１２")]
    [InlineData("run_id", " 1")]
    [InlineData("run_id", "123456789012345678901")]
    [InlineData("run_attempt", "0")]
    [InlineData("run_attempt", "02")]
    [InlineData("run_attempt", "2.0")]
    public async Task Unauthorized_claims_are_forbidden(string name, string value)
    {
        var claims = Claims(); claims[name] = value;
        await Assert.ThrowsAsync<ForbiddenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("repository_id")]
    [InlineData("run_id")]
    [InlineData("run_attempt")]
    public async Task Missing_claim_is_forbidden(string name)
    {
        var claims = Claims(); claims.Remove(name);
        await Assert.ThrowsAsync<ForbiddenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Theory]
    [InlineData("repository", "\"hallipr/test-things\"")]
    [InlineData("repository_id", "\"1437763204\"")]
    [InlineData("run_id", "\"123456789\"")]
    [InlineData("run_attempt", "\"2\"")]
    public async Task Duplicate_raw_JSON_claim_is_forbidden_even_when_values_match(string name, string value)
    {
        var payload = JsonSerializer.Serialize(Claims());
        payload = payload[..^1] + ",\"" + name + "\":" + value + "}";
        await Assert.ThrowsAsync<ForbiddenException>(() => Validator().ValidateAsync(Sign(payload), default));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("repository_id")]
    [InlineData("run_id")]
    [InlineData("run_attempt")]
    public async Task Array_claim_is_forbidden(string name)
    {
        var claims = Claims(); claims[name] = new[] { claims[name] };
        await Assert.ThrowsAsync<ForbiddenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Fact]
    public async Task Numeric_JSON_run_id_is_not_a_canonical_string()
    {
        var claims = Claims(); claims["run_id"] = 123456789;
        await Assert.ThrowsAsync<ForbiddenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Theory]
    [InlineData("iss", "https://attacker.example")]
    [InlineData("aud", "api://other")]
    [InlineData("aud", "api://test/")]
    public async Task Wrong_issuer_or_audience_is_unauthenticated(string name, string value)
    {
        var claims = Claims(); claims[name] = value;
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Fact]
    public async Task Expired_token_is_unauthenticated()
    {
        var claims = Claims(); claims["exp"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Fact]
    public async Task Future_nbf_is_unauthenticated()
    {
        var claims = Claims(); claims["nbf"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Fact]
    public async Task Expiration_is_required()
    {
        var claims = Claims(); claims.Remove("exp");
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(claims)), default));
    }

    [Fact]
    public async Task Bad_signature_is_unauthenticated()
    {
        using var attacker = RSA.Create(2048);
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(Claims()), attacker), default));
    }

    [Fact]
    public async Task Algorithm_is_pinned()
    {
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(Sign(JsonSerializer.Serialize(Claims()), algorithm: "RS512"), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not.a.jwt")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.")]
    public async Task Malformed_or_unsigned_token_is_unauthenticated(string token) =>
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator().ValidateAsync(token, default));

    [Fact]
    public async Task Metadata_failure_is_an_upstream_failure() =>
        await Assert.ThrowsAsync<UpstreamException>(() => Validator(new TestConfiguration(null)).ValidateAsync(Sign(JsonSerializer.Serialize(Claims())), default));

    [Fact]
    public async Task Unknown_key_requests_refresh_but_does_not_authorize()
    {
        var config = new TestConfiguration(new OpenIdConnectConfiguration
        {
            Issuer = BrokerOptions.Issuer,
            SigningKeys = { new RsaSecurityKey(rsa) { KeyId = "rotated-key" } }
        });
        // Same RSA key could be tried without kid matching; a genuinely different key
        // ensures signature verification fails and requests discovery refresh.
        using var unknown = RSA.Create(2048);
        await Assert.ThrowsAsync<InvalidTokenException>(() => Validator(config).ValidateAsync(Sign(JsonSerializer.Serialize(Claims()), unknown), default));
        Assert.True(config.Refreshed);
    }

    public void Dispose() => rsa.Dispose();

    private sealed class TestConfiguration(OpenIdConnectConfiguration? value) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public bool Refreshed { get; private set; }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            value is null ? throw new HttpRequestException("metadata unavailable") : Task.FromResult(value);
        public void RequestRefresh() => Refreshed = true;
    }
}
