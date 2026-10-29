using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Sas;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DirectoryAccess.Functions;

public sealed class AzureRunDirectoryStore(DataLakeServiceClient service, ILogger<AzureRunDirectoryStore> logger) : IRunDirectoryStore
{
    public async Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var directory = service.GetFileSystemClient(BrokerOptions.FileSystem).GetDirectoryClient(path);
            logger.LogInformation("Checking invocation directory {DirectoryUrl}", directory.Uri.AbsoluteUri);
            var properties = await directory.GetPropertiesAsync(cancellationToken: cancellationToken);
            logger.LogInformation("Invocation path properties retrieved; is directory {IsDirectory}, storage request ID {StorageRequestId}",
                properties.Value.IsDirectory, properties.GetRawResponse().Headers.RequestId);
            return properties.Value.IsDirectory == true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404 &&
            ex.ErrorCode is "PathNotFound" or "BlobNotFound")
        {
            logger.LogInformation("Invocation path not found; storage HTTP {StorageStatus}, error {StorageErrorCode}", ex.Status, ex.ErrorCode);
            return false;
        }
        // Container/account disappearance, access denied, throttling and outages
        // propagate, never masquerading as a missing authorization directory.
    }

    public async Task<InvocationAuthorization?> GetAuthorizationAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = service.GetFileSystemClient(BrokerOptions.FileSystem)
                .GetFileClient($"{path}/{BrokerOptions.AuthorizationFile}");
            logger.LogInformation("Reading trusted invocation mapping {AuthorizationBlobUrl}", file.Uri.AbsoluteUri);
            var response = await file.ReadStreamingAsync(cancellationToken);
            using var download = response.Value;
            var bytes = new byte[4097];
            var length = await download.Content.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, false, cancellationToken);
            if (length > 4096)
            {
                logger.LogError("Invocation mapping rejected: exceeds the 4096-byte limit");
                throw new UpstreamException();
            }
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                json.RootElement.EnumerateObject().Count() != 4)
            {
                logger.LogError("Invocation mapping rejected: expected a JSON object with exactly four fields");
                throw new UpstreamException();
            }
            string Required(string name)
            {
                var values = json.RootElement.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
                if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String)
                {
                    logger.LogError("Invocation mapping rejected: {MappingField} must occur exactly once as a string", name);
                    throw new UpstreamException();
                }
                return values[0].Value.GetString()!;
            }
            var authorization = new InvocationAuthorization(Required("invocationId"),
                new RunIdentity(Required("repositoryId"), Required("runId"), Required("runAttempt")));
            logger.LogInformation("Invocation mapping parsed successfully; {MappingBytes} bytes, storage request ID {StorageRequestId}",
                length, response.GetRawResponse().Headers.RequestId);
            return authorization;
        }
        catch (RequestFailedException ex) when (ex.Status == 404 &&
            ex.ErrorCode is "PathNotFound" or "BlobNotFound")
        {
            logger.LogInformation("Invocation authorization blob not found; storage HTTP {StorageStatus}, error {StorageErrorCode}", ex.Status, ex.ErrorCode);
            return null;
        }
        catch (JsonException)
        {
            logger.LogError("Invocation mapping rejected: malformed JSON");
            throw new UpstreamException();
        }
    }

    public async Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn,
        DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        logger.LogInformation("Requesting a user delegation key for storage account {StorageAccount}; starts {StartsOn}, expires {ExpiresOn}",
            service.AccountName, startsOn, expiresOn);
        var key = await service.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken);
        if (key.Value.SignedStartsOn > startsOn || key.Value.SignedExpiresOn < expiresOn)
        {
            logger.LogError("User delegation key validity does not cover the requested SAS interval; key starts {KeyStartsOn}, expires {KeyExpiresOn}",
                key.Value.SignedStartsOn, key.Value.SignedExpiresOn);
            throw new UpstreamException();
        }
        logger.LogInformation("User delegation key acquired; validity checked, storage request ID {StorageRequestId}",
            key.GetRawResponse().Headers.RequestId);
        var sas = DirectorySas.Create(path, startsOn, expiresOn)
            .ToSasQueryParameters(key.Value, service.AccountName).ToString();
        var directoryUrl = service.GetFileSystemClient(BrokerOptions.FileSystem).GetDirectoryClient(path).Uri.AbsoluteUri;
        logger.LogInformation("Directory SAS issued; URL {DirectoryUrl} (SAS query omitted), resource d, permissions rl, HTTPS only, starts {StartsOn}, expires {ExpiresOn}, lifetime seconds {LifetimeSeconds}",
            directoryUrl, startsOn, expiresOn, (expiresOn - startsOn).TotalSeconds);
        return new DirectoryGrant
        {
            DirectoryUrl = directoryUrl,
            SasToken = sas,
            StartsOn = startsOn,
            ExpiresOn = expiresOn
        };
    }
}

public static class DirectorySas
{
    public static DataLakeSasBuilder Create(string path, DateTimeOffset startsOn, DateTimeOffset expiresOn)
    {
        var sas = new DataLakeSasBuilder
        {
            FileSystemName = BrokerOptions.FileSystem,
            Path = path,
            IsDirectory = true,
            Protocol = SasProtocol.Https,
            StartsOn = startsOn,
            ExpiresOn = expiresOn
        };
        sas.SetPermissions(DataLakeSasPermissions.Read | DataLakeSasPermissions.List);
        return sas;
    }
}
