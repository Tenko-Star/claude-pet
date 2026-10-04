using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskPet.App.Animation;
using DeskPet.App.Characters;
using DeskPet.App.Companions;
using DeskPet.App.Rendering;
using DeskPet.App.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatusHub.Contracts;

namespace DeskPet.App;

/// <summary>
/// Transparent, borderless, topmost window that shows the character and its companions. All animation decisions
/// live in <see cref="PetController"/>, <see cref="SpritePlayer"/> and <see cref="CompanionPlayer"/>; this class
/// renders, handles input and swaps the loaded character when the user picks another one or its files change on disk.
/// The image is the manifest stage plus the room the companions need around it.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMinutes(1);

    private readonly CharacterCatalog _catalog;
    private readonly PetSessionFactory _sessionFactory;
    private readonly WindowStateStore _stateStore;
    private readonly AutoStart _autoStart;
    private readonly ILogger<MainWindow> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly List<MenuItem> _scaleItems = [];
    private readonly MenuItem _characterMenu = new() { Header = "角色" };
    private readonly MenuItem _autoStartItem = new() { Header = "开机自启", IsCheckable = true };

    private readonly CompanionPlayer _companions;

    private PetSession _session;
    private PixelRect _padding;
    private int _scale;
    private DpiScale _dpi = new(1, 1);
    private IReadOnlyList<SpritePlacement>? _shownSprites;
    private IReadOnlyList<CompanionDraw>? _shownCompanions;

    /// <exception cref="InvalidDataException">No character could be loaded.</exception>
    public MainWindow(
        CharacterCatalog catalog,
        PetSessionFactory sessionFactory,
        WindowStateStore stateStore,
        AutoStart autoStart,
        IOptions<DeskPetOptions> options,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _catalog = catalog;
        _sessionFactory = sessionFactory;
        _stateStore = stateStore;
        _autoStart = autoStart;
        _logger = logger;

        var saved = _stateStore.Load();
        _session = LoadInitialSession(saved?.Character);
        var layout = MeasureLayout(_session.Sprites);
        _padding = layout.Padding(_session.Sprites.Manifest.StageWidth, _session.Sprites.Manifest.StageHeight);
        _companions = new CompanionPlayer(
            CompanionArt.Load(Path.Combine(AppContext.BaseDirectory, CompanionArt.DirectoryName)), layout, _clock.Elapsed);
        _scale = WindowGeometry.ClampScale(saved?.Scale ?? options.Value.Scale);
        if (saved is not null)
        {
            Left = saved.Left;
            Top = saved.Top;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        _timer.Tick += (_, _) => RenderFrame();

        _autoStartItem.Click += (_, _) => SetAutoStart(_autoStartItem.IsChecked);
        ContextMenu = BuildContextMenu();

        _catalog.Changed += (_, e) => Dispatcher.InvokeAsync(() => OnCatalogChanged(e.Ids));
        _catalog.StartWatching();

        SourceInitialized += (_, _) => OnSourceInitialized(saved);
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    /// <summary>Raised when the user picks "退出" from a menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised after a different character, or a reloaded version of it, is shown.</summary>
    public event EventHandler? CharacterChanged;

    public int Scale => _scale;

    public SpriteLibrary Sprites => _session.Sprites;

    public CharacterInfo CurrentCharacter => _session.Character;

    public IReadOnlyList<CharacterInfo> Characters => _catalog.Characters;

    public bool IsAutoStartEnabled => _autoStart.IsEnabled();

    public void SetScale(int scale)
    {
        scale = WindowGeometry.ClampScale(scale);
        if (scale == _scale)
        {
            return;
        }
        _scale = scale;
        ApplySize();
        SavePlacement();
    }

    /// <summary>Shows another character from the catalog. Load errors are reported and the current one stays.</summary>
    public void SwitchCharacter(string id)
    {
        if (string.Equals(id, _session.Character.Id, StringComparison.OrdinalIgnoreCase) || _catalog.Find(id) is not { } character)
        {
            return;
        }
        try
        {
            ShowSession(_sessionFactory.Create(character, _clock.Elapsed));
        }
        catch (Exception ex) when (PetSessionFactory.IsLoadError(ex))
        {
            _logger.LogError(ex, "Failed to load character '{Id}' from {Path}.", character.Id, character.Directory);
            MessageBox.Show($"无法加载角色“{character.Name}”：\n{ex.Message}", "DeskPet", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens the per-user character folder in Explorer, creating it first.</summary>
    public void OpenCharacterFolder()
    {
        try
        {
            Directory.CreateDirectory(_catalog.UserRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_catalog.UserRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open {Path}.", _catalog.UserRoot);
        }
    }

    public void SetAutoStart(bool enabled)
    {
        try
        {
            _autoStart.Set(enabled, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskPet.App.exe"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not change the start-at-login setting.");
        }
    }

    /// <summary>Shows the latest aggregated status and the active sessions. Must be called on the UI thread.</summary>
    public void ApplySnapshot(StatusSnapshot snapshot)
    {
        _session.Pet.ApplySnapshot(snapshot, _clock.Elapsed);
        _companions.Sync(snapshot.Sessions, _clock.Elapsed);
        RenderFrame();
    }

    /// <summary>Plays a one-shot status event. Must be called on the UI thread.</summary>
    public void ApplyEvent(StatusEvent statusEvent)
    {
        _session.Pet.ApplyEvent(statusEvent, _clock.Elapsed);
        RenderFrame();
    }

    /// <summary>The status stream was lost. Must be called on the UI thread.</summary>
    public void ApplyDisconnected()
    {
        _session.Pet.ApplyDisconnected(_clock.Elapsed);
        _companions.Clear(_clock.Elapsed);
        RenderFrame();
    }

    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi;
        ApplySize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _timer.Stop();
        SavePlacement();
        base.OnClosing(e);
    }

    // Saved character, then the default one, then any other; a broken package is skipped.
    private PetSession LoadInitialSession(string? savedId)
    {
        var candidates = new List<CharacterInfo>();
        foreach (var id in new[] { savedId, CharacterCatalog.DefaultCharacterId })
        {
            if (id is not null && _catalog.Find(id) is { } character && !candidates.Contains(character))
            {
                candidates.Add(character);
            }
        }
        candidates.AddRange(_catalog.Characters.Where(c => !candidates.Contains(c)));

        foreach (var character in candidates)
        {
            try
            {
                return _sessionFactory.Create(character, _clock.Elapsed);
            }
            catch (Exception ex) when (PetSessionFactory.IsLoadError(ex))
            {
                _logger.LogError(ex, "Failed to load character '{Id}' from {Path}.", character.Id, character.Directory);
            }
        }
        throw new InvalidDataException(
            $"No usable character found in '{_catalog.BuiltInRoot}' or '{_catalog.UserRoot}'.");
    }

    private void ShowSession(PetSession session)
    {
        session.Pet.ContinueFrom(_session.Pet, _clock.Elapsed);
        _session = session;
        var layout = MeasureLayout(session.Sprites);
        _padding = layout.Padding(session.Sprites.Manifest.StageWidth, session.Sprites.Manifest.StageHeight);
        _companions.SetLayout(layout, _clock.Elapsed);
        ApplySize();
        SavePlacement();
        CharacterChanged?.Invoke(this, EventArgs.Empty);
    }

    // Hot reload: reloads the shown character when its folder changed or it was overridden or removed.
    private void OnCatalogChanged(IReadOnlySet<string> changedIds)
    {
        _catalog.Refresh();
        var current = _session.Character;
        var target = _catalog.Find(current.Id);
        if (target is null)
        {
            target = _catalog.Find(CharacterCatalog.DefaultCharacterId) ?? _catalog.Characters.FirstOrDefault();
            if (target is null)
            {
                _logger.LogWarning("Character '{Id}' was removed and no other character is available.", current.Id);
                return;
            }
        }
        else if (!changedIds.Contains(target.Id) && target == current)
        {
            return;
        }

        try
        {
            ShowSession(_sessionFactory.Create(target, _clock.Elapsed));
            _logger.LogInformation("Reloaded character '{Id}' from {Path}.", target.Id, target.Directory);
        }
        catch (Exception ex) when (PetSessionFactory.IsLoadError(ex))
        {
            // Often a package that is still being copied; the next change event retries.
            _logger.LogWarning(ex, "Could not reload character '{Id}'; keeping the current one.", target.Id);
        }
    }

    private void OnSourceInitialized(WindowPlacement? saved)
    {
        _dpi = VisualTreeHelper.GetDpi(this);
        ApplySize();

        var width = SpriteImage.Width;
        var height = SpriteImage.Height;
        var onScreen = saved is not null && WindowGeometry.IsVisible(
            saved.Left, saved.Top, width, height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!onScreen)
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - width - 24;
            Top = work.Bottom - height;
        }
        SnapPosition();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove runs a modal move loop and returns once the button is released.
        DragMove();
        SnapPosition();
        SavePlacement();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var scaleMenu = new MenuItem { Header = "缩放" };
        for (var s = WindowGeometry.MinScale; s <= WindowGeometry.MaxScale; s++)
        {
            var value = s;
            var item = new MenuItem { Header = $"{value}×", IsCheckable = true };
            item.Click += (_, _) => SetScale(value);
            _scaleItems.Add(item);
            scaleMenu.Items.Add(item);
        }
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => RequestExit();

        menu.Items.Add(scaleMenu);
        menu.Items.Add(_characterMenu);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) =>
        {
            for (var i = 0; i < _scaleItems.Count; i++)
            {
                _scaleItems[i].IsChecked = WindowGeometry.MinScale + i == _scale;
            }
            RebuildCharacterMenu();
            _autoStartItem.IsChecked = IsAutoStartEnabled;
        };
        return menu;
    }

    private void RebuildCharacterMenu()
    {
        _characterMenu.Items.Clear();
        foreach (var character in _catalog.Characters)
        {
            var id = character.Id;
            var item = new MenuItem
            {
                Header = character.Name,
                IsCheckable = true,
                IsChecked = string.Equals(id, _session.Character.Id, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => SwitchCharacter(id);
            _characterMenu.Items.Add(item);
        }
        _characterMenu.Items.Add(new Separator());
        var openItem = new MenuItem { Header = "打开角色目录" };
        openItem.Click += (_, _) => OpenCharacterFolder();
        _characterMenu.Items.Add(openItem);
    }

    // The pet's silhouette over every character layer it can show, placed on the manifest stage.
    private static CompanionLayout MeasureLayout(SpriteLibrary sprites) =>
        CompanionLayout.Measure(sprites.Manifest.CharacterFiles.Select(file => sprites[file]), sprites.Manifest.CharacterOffset);

    private int ImageWidth => _session.Sprites.Manifest.StageWidth + _padding.Left + _padding.Right;

    private int ImageHeight => _session.Sprites.Manifest.StageHeight + _padding.Top + _padding.Bottom;

    // Sizes the image to the stage so one sprite pixel covers exactly _scale x _scale device pixels.
    private void ApplySize()
    {
        SpriteImage.Width = WindowGeometry.ToDips(ImageWidth * _scale, _dpi.DpiScaleX);
        SpriteImage.Height = WindowGeometry.ToDips(ImageHeight * _scale, _dpi.DpiScaleY);
        _shownSprites = null;
        RenderFrame();
        SnapPosition();
    }

    private void RenderFrame()
    {
        _timer.Stop();
        var now = _clock.Elapsed;
        var frame = _session.Pet.Evaluate(now);
        var companions = _companions.Evaluate(now);

        if (_shownSprites is null || !_shownSprites.SequenceEqual(frame.Sprites)
            || _shownCompanions is null || !_shownCompanions.SequenceEqual(companions.Draws))
        {
            // The pet stage sits inside the padding; companions draw on top of it.
            var sprites = _session.Sprites;
            var layers = frame.Sprites
                .Select(p =>
                {
                    var buffer = sprites[p.File];
                    return new PlacedBuffer(buffer, p.X + _padding.Left, (p.AlignBottom ? p.Y - buffer.Height : p.Y) + _padding.Top);
                })
                .Concat(companions.Draws.Select(d => new PlacedBuffer(d.Buffer, d.X + _padding.Left, d.Y + _padding.Top)));
            var scaled = PixelCompositor.ScaleNearest(PixelCompositor.ComposePlaced(ImageWidth, ImageHeight, layers), _scale);
            // Bitmap DPI matches the monitor so the image maps 1:1 onto device pixels.
            var bitmap = BitmapSource.Create(
                scaled.Width, scaled.Height, 96 * _dpi.DpiScaleX, 96 * _dpi.DpiScaleY,
                PixelFormats.Pbgra32, null, scaled.Pbgra, scaled.Stride);
            bitmap.Freeze();
            SpriteImage.Source = bitmap;
            _shownSprites = frame.Sprites;
            _shownCompanions = companions.Draws;
        }

        var next = frame.NextChangeAt < companions.NextChangeAt ? frame.NextChangeAt : companions.NextChangeAt;
        if (next != TimeSpan.MaxValue)
        {
            var delay = next - _clock.Elapsed;
            _timer.Interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay < MaxTimerDelay ? delay : MaxTimerDelay;
            _timer.Start();
        }
    }

    private void SnapPosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }
        Left = WindowGeometry.SnapToDevicePixel(Left, _dpi.DpiScaleX);
        Top = WindowGeometry.SnapToDevicePixel(Top, _dpi.DpiScaleY);
    }

    private void SavePlacement()
    {
        if (!double.IsNaN(Left) && !double.IsNaN(Top))
        {
            _stateStore.Save(new WindowPlacement(Left, Top, _scale, _session.Character.Id));
        }
    }
}
