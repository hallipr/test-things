using Azure.Identity;
using Azure.Storage.Files.DataLake;
using DirectoryAccess.Functions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var options = new BrokerOptions
{
    Audience = Environment.GetEnvironmentVariable("GitHubOidcAudience") ?? "",
    StorageUrl = new Uri(Environment.GetEnvironmentVariable("RunStorageAccountUrl")
        ?? throw new InvalidOperationException("RunStorageAccountUrl is required."))
};
options.Validate();
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(GitHubTokenValidator.CreateConfigurationManager());
        services.AddSingleton<IGitHubTokenValidator, GitHubTokenValidator>();
        // Developer credentials are opt-in; production uses only the configured identity.
        Azure.Core.TokenCredential credential =
            Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") == "Development"
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
                    Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
                    ?? throw new InvalidOperationException("AZURE_CLIENT_ID is required.")));
        services.AddSingleton(new DataLakeServiceClient(options.StorageUrl, credential));
        services.AddSingleton<IRunDirectoryStore, AzureRunDirectoryStore>();
        services.AddSingleton<DirectoryAccessBroker>();
    })
    .Build();
host.Run();
