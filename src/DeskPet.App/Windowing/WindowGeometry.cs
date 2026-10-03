namespace DeskPet.App.Windowing;

/// <summary>Pure helpers for placing the window on whole device pixels.</summary>
public static class WindowGeometry
{
    public const int MinScale = 1;
    public const int MaxScale = 6;

    public static int ClampScale(int scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>Converts a device-pixel length to DIPs for the given DPI scale (1.0 = 96 DPI).</summary>
    public static double ToDips(int devicePixels, double dpiScale) => devicePixels / dpiScale;

    /// <summary>Rounds a DIP coordinate so it lands exactly on a device pixel.</summary>
    public static double SnapToDevicePixel(double dips, double dpiScale) => Math.Round(dips * dpiScale) / dpiScale;

    /// <summary>True when at least <paramref name="minVisible"/> DIPs of the window overlap the screen area in both axes.</summary>
    public static bool IsVisible(
        double left, double top, double width, double height,
        double screenLeft, double screenTop, double screenWidth, double screenHeight,
        double minVisible = 16)
    {
        var overlapX = Math.Min(left + width, screenLeft + screenWidth) - Math.Max(left, screenLeft);
        var overlapY = Math.Min(top + height, screenTop + screenHeight) - Math.Max(top, screenTop);
        return overlapX >= Math.Min(minVisible, width) && overlapY >= Math.Min(minVisible, height);
    }
}
