using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskPet.App.Animation;
using DeskPet.App.Rendering;
using DeskPet.App.Windowing;
using Microsoft.Extensions.Options;
using StatusHub.Contracts;

namespace DeskPet.App;

/// <summary>
/// Transparent, borderless, topmost window that shows the character. All animation decisions
/// live in <see cref="PetController"/> and <see cref="SpritePlayer"/>; this class only renders and handles input.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMinutes(1);

    private readonly PetController _pet;
    private readonly SpriteLibrary _sprites;
    private readonly WindowStateStore _stateStore;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly MenuItem _talkItem;
    private readonly List<MenuItem> _scaleItems = [];

    private int _scale;
    private DpiScale _dpi = new(1, 1);
    private IReadOnlyList<SpritePlacement>? _shownSprites;

    public MainWindow(PetController pet, SpriteLibrary sprites, WindowStateStore stateStore, IOptions<DeskPetOptions> options)
    {
        InitializeComponent();
        _pet = pet;
        _sprites = sprites;
        _stateStore = stateStore;

        var saved = _stateStore.Load();
        _scale = WindowGeometry.ClampScale(saved?.Scale ?? options.Value.Scale);
        if (saved is not null)
        {
            Left = saved.Left;
            Top = saved.Top;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        _timer.Tick += (_, _) => RenderFrame();

        _talkItem = new MenuItem { Header = "说话", IsCheckable = true };
        _talkItem.Click += (_, _) => SetTalking(_talkItem.IsChecked);
        ContextMenu = BuildContextMenu();

        SourceInitialized += (_, _) => OnSourceInitialized(saved);
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    /// <summary>Raised when the user picks "退出" from a menu.</summary>
    public event EventHandler? ExitRequested;

    public int Scale => _scale;

    public bool IsTalking => _pet.IsTalking;

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

    public void SetTalking(bool talking)
    {
        _pet.SetTalking(talking, _clock.Elapsed);
        RenderFrame();
    }

    /// <summary>Shows the latest aggregated status. Must be called on the UI thread.</summary>
    public void ApplySnapshot(StatusSnapshot snapshot)
    {
        _pet.ApplySnapshot(snapshot, _clock.Elapsed);
        RenderFrame();
    }

    /// <summary>Plays a one-shot status event. Must be called on the UI thread.</summary>
    public void ApplyEvent(StatusEvent statusEvent)
    {
        _pet.ApplyEvent(statusEvent, _clock.Elapsed);
        RenderFrame();
    }

    /// <summary>The status stream was lost. Must be called on the UI thread.</summary>
    public void ApplyDisconnected()
    {
        _pet.ApplyDisconnected(_clock.Elapsed);
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
        menu.Items.Add(_talkItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) =>
        {
            for (var i = 0; i < _scaleItems.Count; i++)
            {
                _scaleItems[i].IsChecked = WindowGeometry.MinScale + i == _scale;
            }
            _talkItem.IsChecked = _pet.IsTalking;
        };
        return menu;
    }

    // Sizes the image to the stage so one sprite pixel covers exactly _scale x _scale device pixels.
    private void ApplySize()
    {
        var manifest = _sprites.Manifest;
        SpriteImage.Width = WindowGeometry.ToDips(manifest.StageWidth * _scale, _dpi.DpiScaleX);
        SpriteImage.Height = WindowGeometry.ToDips(manifest.StageHeight * _scale, _dpi.DpiScaleY);
        _shownSprites = null;
        RenderFrame();
        SnapPosition();
    }

    private void RenderFrame()
    {
        _timer.Stop();
        var now = _clock.Elapsed;
        var frame = _pet.Evaluate(now);

        if (_shownSprites is null || !_shownSprites.SequenceEqual(frame.Sprites))
        {
            var scaled = PixelCompositor.ScaleNearest(_sprites.Compose(frame.Sprites), _scale);
            // Bitmap DPI matches the monitor so the image maps 1:1 onto device pixels.
            var bitmap = BitmapSource.Create(
                scaled.Width, scaled.Height, 96 * _dpi.DpiScaleX, 96 * _dpi.DpiScaleY,
                PixelFormats.Pbgra32, null, scaled.Pbgra, scaled.Stride);
            bitmap.Freeze();
            SpriteImage.Source = bitmap;
            _shownSprites = frame.Sprites;
        }

        if (frame.NextChangeAt != TimeSpan.MaxValue)
        {
            var delay = frame.NextChangeAt - _clock.Elapsed;
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
            _stateStore.Save(new WindowPlacement(Left, Top, _scale));
        }
    }
}
