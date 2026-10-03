using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using StatusHub.Service.HookIngest;

namespace StatusHub.Service;

public static class ServiceHost
{
    /// <summary>Builds the service host. Exposed so tests can start it with overridden configuration.</summary>
    public static WebApplication Build(string[] args)
    {
        // Slim builder: generic host plus Kestrel and routing, without the MVC/static-file/HTTPS defaults.
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddOptions<HookIngestOptions>()
            .Bind(builder.Configuration.GetSection(HookIngestOptions.SectionName))
            .Validate(o => o.Port is >= 0 and <= IPEndPoint.MaxPort, "HookIngest:Port must be 0-65535.")
            .ValidateOnStart();

        // Bind to the IPv4 loopback only; never an external interface.
        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<HookIngestOptions>>((kestrel, ingest) =>
                kestrel.Listen(IPAddress.Loopback, ingest.Value.Port));

        builder.Services.AddSingleton<HookEventLog>();
        builder.Services.AddHostedService<Worker>();

        var app = builder.Build();
        app.MapHookIngest();
        return app;
    }
}
