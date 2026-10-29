using System.Net;
using System.Diagnostics;
using Azure;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DirectoryAccess.Functions;

public sealed class DirectoryAccessFunction(DirectoryAccessBroker broker, ILogger<DirectoryAccessFunction> logger)
{
    [Function("DirectoryAccess")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "directory-access/{invocationId}")] HttpRequestData request,
        string invocationId,
        CancellationToken cancellationToken)
    {
        // The trigger is anonymous only at the Functions key layer. Workload JWT
        // validation and the operator-written invocation mapping are mandatory.
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["FunctionInvocationId"] = request.FunctionContext.InvocationId
        });
        var elapsed = Stopwatch.StartNew();
        logger.LogInformation("Directory access HTTP request received; method {HttpMethod}", request.Method);
        var response = request.CreateResponse();
        response.Headers.Add("Cache-Control", "no-store");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        try
        {
            if (!request.Headers.TryGetValues("Authorization", out var headers))
                throw new InvalidTokenException();
            var values = headers.ToArray();
            if (values.Length != 1 || !values[0].StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidTokenException();
            var token = values[0][7..];
            var grant = await broker.IssueAsync(token, invocationId, cancellationToken);
            response.StatusCode = HttpStatusCode.OK;
            await response.WriteStringAsync(System.Text.Json.JsonSerializer.Serialize(grant,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            logger.LogInformation("Directory access response authorized; directory {DirectoryUrl}, expires {ExpiresOn}; credentials omitted from logs",
                grant.DirectoryUrl, grant.ExpiresOn);
        }
        catch (InvalidTokenException) { await Error(response, HttpStatusCode.Unauthorized, "invalid_token"); }
        catch (ForbiddenException) { await Error(response, HttpStatusCode.Forbidden, "not_authorized"); }
        catch (UpstreamException) { await Error(response, HttpStatusCode.ServiceUnavailable, "identity_or_storage_unavailable"); }
        catch (RequestFailedException ex)
        {
            logger.LogError("Azure Storage request failed; storage HTTP {StorageStatus}, error {StorageErrorCode}", ex.Status, ex.ErrorCode);
            var status = ex.Status is 408 or 429 or >= 500 or 0
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadGateway;
            await Error(response, status, "storage_failure");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Upstream HTTP request failed; status {UpstreamStatus}", ex.StatusCode);
            await Error(response, HttpStatusCode.ServiceUnavailable, "upstream_unavailable");
        }
        catch (AuthenticationFailedException)
        {
            logger.LogError("Managed identity could not authenticate to Azure Storage");
            await Error(response, HttpStatusCode.ServiceUnavailable, "storage_identity_unavailable");
        }
        catch (IOException)
        {
            logger.LogError("Azure Storage response stream could not be read");
            await Error(response, HttpStatusCode.ServiceUnavailable, "storage_read_failure");
        }
        logger.LogInformation("Directory access HTTP request completed; status {HttpStatus}, duration milliseconds {DurationMs}",
            (int)response.StatusCode, elapsed.ElapsedMilliseconds);
        return response;
    }

    private async Task Error(HttpResponseData response, HttpStatusCode status, string code)
    {
        logger.LogWarning("Directory grant rejected: {StatusCode} {ErrorCode}", (int)status, code);
        response.StatusCode = status;
        if (status == HttpStatusCode.Unauthorized)
            response.Headers.Add("WWW-Authenticate", "Bearer");
        await response.WriteStringAsync($"{{\"error\":\"{code}\"}}");
    }
}
