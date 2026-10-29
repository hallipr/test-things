using System.Net;
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
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "directory-access")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        // The trigger is anonymous only at the Functions key layer. Workload JWT
        // validation below is mandatory; no body/query input influences authority.
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
            var grant = await broker.IssueAsync(token, cancellationToken);
            response.StatusCode = HttpStatusCode.OK;
            await response.WriteStringAsync(System.Text.Json.JsonSerializer.Serialize(grant,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
        catch (InvalidTokenException) { await Error(response, HttpStatusCode.Unauthorized, "invalid_token"); }
        catch (ForbiddenException) { await Error(response, HttpStatusCode.Forbidden, "not_authorized"); }
        catch (UpstreamException) { await Error(response, HttpStatusCode.ServiceUnavailable, "identity_or_storage_unavailable"); }
        catch (RequestFailedException ex)
        {
            var status = ex.Status is 408 or 429 or >= 500 or 0
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadGateway;
            await Error(response, status, "storage_failure");
        }
        catch (HttpRequestException) { await Error(response, HttpStatusCode.ServiceUnavailable, "upstream_unavailable"); }
        catch (AuthenticationFailedException) { await Error(response, HttpStatusCode.ServiceUnavailable, "storage_identity_unavailable"); }
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
