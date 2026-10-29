using System.Net;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Azure.Storage.Sas;
using DirectoryAccess.Functions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DirectoryAccess.Tests;

public sealed class DirectoryGrantTests
{
    private readonly RecordingLogger<DirectoryAccessBroker> brokerLogs = new();

    [Fact]
    public async Task Broker_matches_trusted_mapping_and_issues_exact_sixty_minutes()
    {
        var store = new TestStore(true);
        var clock = new FixedClock();
        var grant = await new DirectoryAccessBroker(new Validator(), store, clock, brokerLogs).IssueAsync("signed-token", "1");
        Assert.Equal("1", store.Path);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), grant.StartsOn.ToUnixTimeSeconds());
        Assert.Equal(0, grant.StartsOn.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.FromMinutes(60), grant.ExpiresOn - grant.StartsOn);
        Assert.Contains(brokerLogs.Entries, e => e.Message.Contains("Invocation mapping vetting"));
        Assert.Contains(brokerLogs.Entries, e => e.Message.Contains("Invocation authorization approved"));
        Assert.DoesNotContain("signed-token", brokerLogs.Dump);
        Assert.DoesNotContain(grant.SasToken, brokerLogs.Dump);
    }

    [Fact]
    public async Task Missing_directory_never_issues_a_grant()
    {
        var store = new TestStore(false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new DirectoryAccessBroker(new Validator(), store, new FixedClock(), brokerLogs).IssueAsync("signed-token", "1"));
        Assert.False(store.Signed);
    }

    [Fact]
    public async Task Invalid_token_never_contacts_storage()
    {
        var store = new TestStore(true);
        await Assert.ThrowsAsync<InvalidTokenException>(() =>
            new DirectoryAccessBroker(new Validator(false), store, new FixedClock(), brokerLogs).IssueAsync("invalid", "1"));
        Assert.Null(store.Path);
        Assert.False(store.Signed);
    }

    [Fact]
    public void Sas_is_invocation_directory_scoped_https_read_list_with_depth_one()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var end = start.AddMinutes(60);
        var key = DataLakeModelFactory.UserDelegationKey(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            start, end, "b", "2023-11-03", Convert.ToBase64String(new byte[32]));
        var sas = DirectorySas.Create("1", start, end).ToSasQueryParameters(key, "testaccount");
        var query = sas.ToString().Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("d", query["sr"]);
        Assert.Equal("1", query["sdd"]);
        Assert.Equal("rl", query["sp"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal(TimeSpan.FromMinutes(60),
            DateTimeOffset.Parse(query["se"]) - DateTimeOffset.Parse(query["st"]));
        Assert.DoesNotContain("sig=", new DirectoryGrant
        {
            DirectoryUrl = "https://testaccount.dfs.core.windows.net/runs/1",
            SasToken = sas.ToString(), StartsOn = start, ExpiresOn = end
        }.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1/2")]
    [InlineData("../1")]
    [InlineData("_control")]
    [InlineData("1%2F2")]
    [InlineData("１２")]
    [InlineData("9223372036854775808")]
    public async Task Arbitrary_invocation_paths_never_contact_storage(string invocationId)
    {
        var store = new TestStore(true);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new DirectoryAccessBroker(new Validator(), store, new FixedClock(), brokerLogs).IssueAsync("valid", invocationId));
        Assert.Null(store.Path);
        Assert.False(store.Signed);
    }

    [Fact]
    public async Task A_directory_without_an_operator_mapping_does_not_authorize()
    {
        var store = new TestStore(true, hasAuthorization: false);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new DirectoryAccessBroker(new Validator(), store, new FixedClock(), brokerLogs).IssueAsync("valid", "1"));
        Assert.False(store.Signed);
    }

    [Theory]
    [InlineData("2", "1437763204", "123", "2")]
    [InlineData("1", "1437763205", "123", "2")]
    [InlineData("1", "1437763204", "124", "2")]
    [InlineData("1", "1437763204", "123", "1")]
    public async Task Another_invocation_repository_run_or_attempt_is_forbidden(
        string invocationId, string repositoryId, string runId, string attempt)
    {
        var authorization = new InvocationAuthorization(invocationId, new RunIdentity(repositoryId, runId, attempt));
        var store = new TestStore(true, authorization);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new DirectoryAccessBroker(new Validator(), store, new FixedClock(), brokerLogs).IssueAsync("valid", "1"));
        Assert.False(store.Signed);
        Assert.Contains(brokerLogs.Entries, e => e.Level == LogLevel.Warning &&
            e.Message.Contains("trusted mapping does not match"));
    }

    [Fact]
    public async Task Sas_issuance_logs_url_scope_and_expiry_but_never_credentials()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var end = start.AddMinutes(60);
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var xml = $"""
            <UserDelegationKey>
            <SignedOid>11111111-1111-1111-1111-111111111111</SignedOid>
            <SignedTid>22222222-2222-2222-2222-222222222222</SignedTid>
            <SignedStart>{start:yyyy-MM-ddTHH:mm:ssZ}</SignedStart>
            <SignedExpiry>{end:yyyy-MM-ddTHH:mm:ssZ}</SignedExpiry>
            <SignedService>b</SignedService><SignedVersion>2023-11-03</SignedVersion><Value>{key}</Value>
            </UserDelegationKey>
            """;
        var logs = new RecordingLogger<AzureRunDirectoryStore>();
        var grant = await Store(new StorageHandler(HttpStatusCode.OK, payload: xml, contentType: "application/xml"), logs)
            .SignAsync("1", start, end, default);
        Assert.Contains(grant.DirectoryUrl, logs.Dump);
        Assert.Contains("Directory SAS issued", logs.Dump);
        Assert.Contains("resource d, permissions rl", logs.Dump);
        Assert.Contains(logs.Entries, e => e.Properties.TryGetValue("LifetimeSeconds", out var seconds) && Equals(seconds, 3600d));
        Assert.DoesNotContain(key, logs.Dump);
        Assert.DoesNotContain(grant.SasToken, logs.Dump);
        Assert.DoesNotContain("sig=", logs.Dump);
    }

    [Fact]
    public async Task Operator_mapping_is_read_from_the_reserved_blob()
    {
        var handler = new StorageHandler(HttpStatusCode.OK, payload:
            """{"invocationId":"1","repositoryId":"1437763204","runId":"123","runAttempt":"2"}""");
        var authorization = await Store(handler).GetAuthorizationAsync("1", default);
        Assert.Equal(new InvocationAuthorization("1", new RunIdentity("1437763204", "123", "2")), authorization);
        Assert.EndsWith("/runs/1/.authorization.json", handler.RequestPath);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"invocationId":"1","repositoryId":"1437763204","runId":123,"runAttempt":"2"}""")]
    [InlineData("""{"invocationId":"1","repositoryId":"1437763204","runId":"123","runId":"124"}""")]
    [InlineData("""{"invocationId":"1","repositoryId":"1437763204","runId":"123","runAttempt":"2","extra":"unknown"}""")]
    public async Task Corrupt_or_ambiguous_mappings_fail_closed(string payload)
    {
        await Assert.ThrowsAsync<UpstreamException>(() =>
            Store(new StorageHandler(HttpStatusCode.OK, payload: payload)).GetAuthorizationAsync("1", default));
    }

    [Fact]
    public async Task Oversized_mappings_fail_closed() =>
        await Assert.ThrowsAsync<UpstreamException>(() =>
            Store(new StorageHandler(HttpStatusCode.OK, payload: new string(' ', 4097))).GetAuthorizationAsync("1", default));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_does_not_authorize_a_directory(bool isDirectory)
    {
        var store = Store(new StorageHandler(HttpStatusCode.OK, isDirectory: isDirectory));
        Assert.Equal(isDirectory, await store.IsDirectoryAsync("1", default));
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
        {
            await Assert.ThrowsAsync<RequestFailedException>(() => store.IsDirectoryAsync("1", default));
            await Assert.ThrowsAsync<RequestFailedException>(() => store.GetAuthorizationAsync("1", default));
        }
        else
        {
            Assert.False(await store.IsDirectoryAsync("1", default));
            Assert.Null(await store.GetAuthorizationAsync("1", default));
        }
    }

    private static AzureRunDirectoryStore Store(HttpMessageHandler handler, ILogger<AzureRunDirectoryStore>? logger = null)
    {
        var options = new DataLakeClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return new(new DataLakeServiceClient(new Uri("https://testaccount.dfs.core.windows.net"),
            new TestCredential(), options), logger ?? NullLogger<AzureRunDirectoryStore>.Instance);
    }

    private sealed class StorageHandler(HttpStatusCode status, string? code = null, bool isDirectory = false,
        string? payload = null, string? contentType = null)
        : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(payload ?? (code is null ? "" : $"<Error><Code>{code}</Code><Message>Test failure</Message></Error>"),
                    System.Text.Encoding.UTF8, contentType ?? (payload is null ? "application/xml" : "application/json"))
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

    private sealed class TestStore(bool exists, InvocationAuthorization? authorization = null, bool hasAuthorization = true) : IRunDirectoryStore
    {
        public string? Path { get; private set; }
        public bool Signed { get; private set; }
        public Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Path = path;
            return Task.FromResult(exists);
        }
        public Task<InvocationAuthorization?> GetAuthorizationAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(hasAuthorization
                ? authorization ?? new InvocationAuthorization("1", new RunIdentity(BrokerOptions.RepositoryId, "123", "2"))
                : null);
        public Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn,
            CancellationToken cancellationToken)
        {
            Signed = true;
            return Task.FromResult(new DirectoryGrant
            {
                DirectoryUrl = "https://testaccount.dfs.core.windows.net/runs/" + path,
                SasToken = "sig=DO_NOT_LOG_GRANT_SECRET", StartsOn = startsOn, ExpiresOn = expiresOn
            });
        }
    }
}
