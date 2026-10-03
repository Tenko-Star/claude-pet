using DeskPet.App.Windowing;

namespace DeskPet.App.Tests;

public class WindowGeometryTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void Snapped_positions_land_on_whole_device_pixels(double dpi)
    {
        foreach (var dips in new[] { 0.0, 10.3, 101.77, -37.41, 1234.5 })
        {
            var device = WindowGeometry.SnapToDevicePixel(dips, dpi) * dpi;
            Assert.Equal(Math.Round(device), device, 9);
        }
    }

    [Theory]
    [InlineData(1.0, 3, 357)]
    [InlineData(1.25, 3, 357)]
    [InlineData(1.5, 2, 238)]
    public void Dip_size_maps_back_to_exact_device_pixels(double dpi, int scale, int expectedDevice)
    {
        var dips = WindowGeometry.ToDips(119 * scale, dpi);
        Assert.Equal(expectedDevice, dips * dpi, 9);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 6)]
    public void ClampScale_keeps_scale_in_range(int input, int expected) =>
        Assert.Equal(expected, WindowGeometry.ClampScale(input));

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(1900, 1000, true)]
    [InlineData(1910, 100, false)]
    [InlineData(-300, 100, false)]
    [InlineData(100, 5000, false)]
    public void IsVisible_requires_some_overlap_with_the_screen(double left, double top, bool expected) =>
        Assert.Equal(expected, WindowGeometry.IsVisible(left, top, 300, 300, 0, 0, 1920, 1080));
}

public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskpet-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "sub", "window.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_loads_as_null()
    {
        var store = new WindowStateStore(new RecordingLogger<WindowStateStore>(), FilePath);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new WindowStateStore(new RecordingLogger<WindowStateStore>(), FilePath);
        store.Save(new WindowPlacement(123.4, -56.8, 4));
        Assert.Equal(new WindowPlacement(123.4, -56.8, 4), store.Load());
    }

    [Fact]
    public void Corrupt_file_loads_as_null_and_logs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var logger = new RecordingLogger<WindowStateStore>();

        Assert.Null(new WindowStateStore(logger, FilePath).Load());
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Out_of_range_scale_is_clamped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"Left":1,"Top":2,"Scale":50}""");
        Assert.Equal(new WindowPlacement(1, 2, WindowGeometry.MaxScale), new WindowStateStore(new RecordingLogger<WindowStateStore>(), FilePath).Load());
    }
}
