using System.IO;
using System.Windows;
using DeskPet.App.Animation;
using DeskPet.App.Rendering;
using DeskPet.App.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeskPet.App;

public partial class App : Application
{
    private IHost? _host;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Services.Configure<DeskPetOptions>(builder.Configuration.GetSection(DeskPetOptions.SectionName));
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DeskPetOptions>>().Value;
            return SpriteLibrary.Load(AssetLocator.FindRuntimeDirectory(options.AssetRoot, AppContext.BaseDirectory));
        });
        builder.Services.AddSingleton(sp => new SpritePlayer(
            sp.GetRequiredService<SpriteLibrary>().Manifest,
            sp.GetRequiredService<ILogger<SpritePlayer>>()));
        builder.Services.AddSingleton(sp => new PetController(
            sp.GetRequiredService<SpritePlayer>(),
            sp.GetRequiredService<IOptions<DeskPetOptions>>().Value.SleepAfter));
        builder.Services.AddSingleton(sp => new WindowStateStore(sp.GetRequiredService<ILogger<WindowStateStore>>()));
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIcon>();

        _host = builder.Build();
        var logger = _host.Services.GetRequiredService<ILogger<App>>();

        MainWindow window;
        try
        {
            window = _host.Services.GetRequiredService<MainWindow>();
            _tray = _host.Services.GetRequiredService<TrayIcon>();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Failed to load the sprite assets.");
            MessageBox.Show(ex.Message, "DeskPet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        window.ExitRequested += async (_, _) => await ExitAsync();
        MainWindow = window;
        window.Show();

        await _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }

    // Closing the main window ends the app (ShutdownMode=OnMainWindowClose).
    private async Task ExitAsync()
    {
        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _host.StopAsync(timeout.Token);
        }
        MainWindow?.Close();
    }
}
