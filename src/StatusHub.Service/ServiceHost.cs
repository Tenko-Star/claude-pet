using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using StatusHub.Contracts;
using StatusHub.Service.HookIngest;
using StatusHub.Service.Status;

namespace StatusHub.Service;

public static class ServiceHost
{
    /// <summary>Windows Service name; must match scripts/install-service.ps1.</summary>
    public const string ServiceName = "ClaudePetStatusHub";

    /// <summary>Builds the service host. Exposed so tests can start it with overridden configuration.</summary>
    public static WebApplication Build(string[] args)
    {
        // Slim builder: generic host plus Kestrel and routing, without the MVC/static-file/HTTPS defaults.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            // The SCM starts services in System32; resolve appsettings.json next to the executable instead.
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });

        // Integrates with the Service Control Manager when started as a Windows Service; no-op otherwise.
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);

        builder.Services.AddOptions<HookIngestOptions>()
            .Bind(builder.Configuration.GetSection(HookIngestOptions.SectionName))
            .Validate(o => o.Port is >= 0 and <= IPEndPoint.MaxPort, "HookIngest:Port must be 0-65535.")
            .ValidateOnStart();

        // Bind to the IPv4 loopback only; never an external interface.
        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<HookIngestOptions>>((kestrel, ingest) =>
                kestrel.Listen(IPAddress.Loopback, ingest.Value.Port));

        builder.Services.AddOptions<StatusOptions>()
            .Bind(builder.Configuration.GetSection(StatusOptions.SectionName))
            .Validate(o => o.SessionTimeout > TimeSpan.Zero, "Status:SessionTimeout must be positive.")
            .Validate(o => o.ExpiryScanInterval > TimeSpan.Zero, "Status:ExpiryScanInterval must be positive.")
            .Validate(o => o.ThinkFallback > TimeSpan.Zero, "Status:ThinkFallback must be positive.")
            .ValidateOnStart();

        builder.Services.AddSingleton<HookEventLog>();

        // Many writers (ingest requests), one reader (the reducer), like a Rust mpsc channel.
        var hookEvents = Channel.CreateUnbounded<HookEvent>(new UnboundedChannelOptions { SingleReader = true });
        builder.Services.AddSingleton(hookEvents.Reader);
        builder.Services.AddSingleton(hookEvents.Writer);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StatusStore>();
        builder.Services.AddSignalR();
        builder.Services.AddHostedService<StatusReducerService>();

        var app = builder.Build();
        // The slim builder does not add WebSockets; without it SignalR falls back to SSE or long polling.
        app.UseWebSockets();
        app.MapHookIngest();
        app.MapHub<StatusStreamHub>(StatusHubProtocol.Path);
        return app;
    }
}
