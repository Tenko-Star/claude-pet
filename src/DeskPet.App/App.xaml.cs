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
    // Per user session. The first process creates it; later ones signal it and exit.
    private const string BringBackEventName = @"Local\ClaudePet.DeskPet.BringBack";

    private IHost? _host;
    private TrayIcon? _tray;
    private EventWaitHandle? _bringBackSignal;
    private RegisteredWaitHandle? _bringBackWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second start brings the running pet back instead of showing another one.
        _bringBackSignal = new EventWaitHandle(false, EventResetMode.AutoReset, BringBackEventName, out var createdNew);
        if (!createdNew)
        {
            _bringBackSignal.Set();
            Shutdown();
            return;
        }

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
        _bringBackWait = ThreadPool.RegisterWaitForSingleObject(
            _bringBackSignal, (_, _) => window.Dispatcher.InvokeAsync(window.BringBack), null, Timeout.Infinite, executeOnlyOnce: false);

        await _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bringBackWait?.Unregister(null);
        _bringBackSignal?.Dispose();
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
