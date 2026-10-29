using System.Net;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Azure.Storage.Sas;
using DirectoryAccess.Functions;
using Xunit;

namespace DirectoryAccess.Tests;

public sealed class DirectoryGrantTests
{
    [Fact]
    public async Task Broker_uses_only_signed_claim_path_and_exact_sixty_minutes()
    {
        var store = new TestStore(true);
        var clock = new FixedClock();
        var grant = await new DirectoryAccessBroker(new Validator(), store, clock).IssueAsync("signed-token");
        Assert.Equal("1437763204/123/2", store.Path);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), grant.StartsOn.ToUnixTimeSeconds());
        Assert.Equal(0, grant.StartsOn.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.FromMinutes(60), grant.ExpiresOn - grant.StartsOn);
    }

    [Fact]
    public async Task Missing_directory_never_issues_a_grant()
    {
        var store = new TestStore(false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new DirectoryAccessBroker(new Validator(), store, new FixedClock()).IssueAsync("signed-token"));
        Assert.False(store.Signed);
    }

    [Fact]
    public async Task Invalid_token_never_contacts_storage()
    {
        var store = new TestStore(true);
        await Assert.ThrowsAsync<InvalidTokenException>(() =>
            new DirectoryAccessBroker(new Validator(false), store, new FixedClock()).IssueAsync("invalid"));
        Assert.Null(store.Path);
        Assert.False(store.Signed);
    }

    [Fact]
    public void Sas_is_directory_scoped_https_read_list_with_depth_three()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var end = start.AddMinutes(60);
        var key = DataLakeModelFactory.UserDelegationKey(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            start, end, "b", "2023-11-03", Convert.ToBase64String(new byte[32]));
        var sas = DirectorySas.Create("1437763204/123/2", start, end).ToSasQueryParameters(key, "testaccount");
        var query = sas.ToString().Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("d", query["sr"]);
        Assert.Equal("3", query["sdd"]);
        Assert.Equal("rl", query["sp"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal(TimeSpan.FromMinutes(60),
            DateTimeOffset.Parse(query["se"]) - DateTimeOffset.Parse(query["st"]));
        Assert.DoesNotContain("sig=", new DirectoryGrant
        {
            DirectoryUrl = "https://testaccount.dfs.core.windows.net/runs/1437763204/123/2",
            SasToken = sas.ToString(), StartsOn = start, ExpiresOn = end
        }.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_does_not_authorize_a_directory(bool isDirectory)
    {
        var store = Store(new StorageHandler(HttpStatusCode.OK, isDirectory: isDirectory));
        Assert.Equal(isDirectory, await store.IsDirectoryAsync("1437763204/123/2", default));
    }

    [Theory]
    [InlineData(404, "PathNotFound", false)]
    [InlineData(404, "BlobNotFound", false)]
    [InlineData(404, "ContainerNotFound", true)]
    [InlineData(403, "AuthorizationPermissionMismatch", true)]
    [InlineData(429, "ServerBusy", true)]
    [InlineData(503, "ServerBusy", true)]
    public async Task Storage_failures_are_not_missing_authorization(int status, string code, bool throws)
    {
        var store = Store(new StorageHandler((HttpStatusCode)status, code));
        if (throws)
            await Assert.ThrowsAsync<RequestFailedException>(() => store.IsDirectoryAsync("1437763204/123/2", default));
        else
            Assert.False(await store.IsDirectoryAsync("1437763204/123/2", default));
    }

    private static AzureRunDirectoryStore Store(HttpMessageHandler handler)
    {
        var options = new DataLakeClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return new(new DataLakeServiceClient(new Uri("https://testaccount.dfs.core.windows.net"),
            new TestCredential(), options));
    }

    private sealed class StorageHandler(HttpStatusCode status, string? code = null, bool isDirectory = false)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(code is null ? "" : $"<Error><Code>{code}</Code><Message>Test failure</Message></Error>",
                    System.Text.Encoding.UTF8, "application/xml")
            };
            if (isDirectory) response.Headers.TryAddWithoutValidation("x-ms-meta-hdi_isfolder", "true");
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "test-request");
            response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            if (code is not null) response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            return Task.FromResult(response);
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1800000000123);
    }

    private sealed class Validator(bool valid = true) : IGitHubTokenValidator
    {
        public Task<RunIdentity> ValidateAsync(string token, CancellationToken cancellationToken) =>
            valid ? Task.FromResult(new RunIdentity(BrokerOptions.RepositoryId, "123", "2")) : throw new InvalidTokenException();
    }

    private sealed class TestStore(bool exists) : IRunDirectoryStore
    {
        public string? Path { get; private set; }
        public bool Signed { get; private set; }
        public Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Path = path;
            return Task.FromResult(exists);
        }
        public Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn,
            CancellationToken cancellationToken)
        {
            Signed = true;
            return Task.FromResult(new DirectoryGrant
            {
                DirectoryUrl = "https://testaccount.dfs.core.windows.net/runs/" + path,
                SasToken = "test", StartsOn = startsOn, ExpiresOn = expiresOn
            });
        }
    }
}
