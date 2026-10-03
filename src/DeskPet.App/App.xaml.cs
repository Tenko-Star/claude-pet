using System.IO;
using System.Windows;
using DeskPet.App.Characters;
using DeskPet.App.Status;
using DeskPet.App.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        builder.Services.AddSingleton(sp => new CharacterCatalog(
            CharacterCatalog.DefaultBuiltInRoot,
            CharacterCatalog.DefaultUserRoot,
            sp.GetRequiredService<ILogger<CharacterCatalog>>()));
        builder.Services.AddSingleton<PetSessionFactory>();
        builder.Services.AddSingleton(sp => new WindowStateStore(sp.GetRequiredService<ILogger<WindowStateStore>>()));
        builder.Services.AddSingleton(_ => new AutoStart());
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddHostedService<StatusClientService>();

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
            logger.LogError(ex, "Failed to load a character.");
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
