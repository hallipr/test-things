using Azure.Identity;
using Azure.Storage.Files.DataLake;
using DirectoryAccess.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;

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
        services.AddApplicationInsightsTelemetryWorkerService(settings => settings.EnableAdaptiveSampling = false);
        services.ConfigureFunctionsApplicationInsights();
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
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("DirectoryAccess.Functions", LogLevel.Information);
        // Override the AI provider's default Warning filter for our categories only.
        logging.AddFilter<ApplicationInsightsLoggerProvider>("DirectoryAccess.Functions", LogLevel.Information);
    })
    .Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DirectoryAccess.Functions.Startup");
var insightsConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"));
logger.LogInformation("Directory access worker started; Application Insights configured {InsightsConfigured}; expected issuer {Issuer}, audience {Audience}, repository ID {RepositoryId}, storage host {StorageHost}; adaptive sampling disabled",
    insightsConfigured, BrokerOptions.Issuer, options.Audience, BrokerOptions.RepositoryId, options.StorageUrl.Host);
if (!insightsConfigured)
    logger.LogWarning("APPLICATIONINSIGHTS_CONNECTION_STRING is missing; configure it to send worker telemetry to Application Insights");
host.Run();
