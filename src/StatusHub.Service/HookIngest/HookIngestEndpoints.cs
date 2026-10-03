using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace StatusHub.Service.HookIngest;

public static class HookIngestEndpoints
{
    public static IEndpointRouteBuilder MapHookIngest(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/hooks/{eventName}", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string eventName,
        HttpRequest request,
        HookEventLog log,
        IHostApplicationLifetime lifetime,
        ILogger<HookEventLog> logger)
    {
        var receivedAt = DateTimeOffset.UtcNow;

        string body;
        using (var reader = new StreamReader(request.Body))
        {
            body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        }

        // Once the body is in hand, record it even if the hook client has already given up;
        // only service shutdown cancels the write.
        await log.AppendAsync(receivedAt, eventName, body, lifetime.ApplicationStopping);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Recorded hook {EventName} ({Length} chars)", eventName, body.Length);
        }

        return Results.NoContent();
    }
}
