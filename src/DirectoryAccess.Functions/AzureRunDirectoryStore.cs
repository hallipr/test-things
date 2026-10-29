using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Sas;
using System.Text.Json;

namespace DirectoryAccess.Functions;

public sealed class AzureRunDirectoryStore(DataLakeServiceClient service) : IRunDirectoryStore
{
    public async Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var directory = service.GetFileSystemClient(BrokerOptions.FileSystem).GetDirectoryClient(path);
            var properties = await directory.GetPropertiesAsync(cancellationToken: cancellationToken);
            return properties.Value.IsDirectory == true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404 &&
            ex.ErrorCode is "PathNotFound" or "BlobNotFound")
        {
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
            using var download = (await file.ReadStreamingAsync(cancellationToken)).Value;
            var bytes = new byte[4097];
            var length = await download.Content.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, false, cancellationToken);
            if (length > 4096)
                throw new UpstreamException();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                json.RootElement.EnumerateObject().Count() != 4)
                throw new UpstreamException();
            string Required(string name)
            {
                var values = json.RootElement.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
                if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String)
                    throw new UpstreamException();
                return values[0].Value.GetString()!;
            }
            return new InvocationAuthorization(Required("invocationId"),
                new RunIdentity(Required("repositoryId"), Required("runId"), Required("runAttempt")));
        }
        catch (RequestFailedException ex) when (ex.Status == 404 &&
            ex.ErrorCode is "PathNotFound" or "BlobNotFound")
        {
            return null;
        }
        catch (JsonException)
        {
            throw new UpstreamException();
        }
    }

    public async Task<DirectoryGrant> SignAsync(string path, DateTimeOffset startsOn,
        DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        var key = await service.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken);
        if (key.Value.SignedStartsOn > startsOn || key.Value.SignedExpiresOn < expiresOn)
            throw new UpstreamException();
        var sas = DirectorySas.Create(path, startsOn, expiresOn)
            .ToSasQueryParameters(key.Value, service.AccountName).ToString();
        return new DirectoryGrant
        {
            DirectoryUrl = service.GetFileSystemClient(BrokerOptions.FileSystem).GetDirectoryClient(path).Uri.AbsoluteUri,
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
