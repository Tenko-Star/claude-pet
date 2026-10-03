using Microsoft.Win32;

namespace DeskPet.App.Windowing;

/// <summary>
/// Start-at-login switch: a value in the current user's <c>Run</c> key. The installer's
/// "start at login" task writes the same value, so both stay in sync.
/// </summary>
public sealed class AutoStart(string runKeyPath = AutoStart.DefaultRunKeyPath, string valueName = AutoStart.DefaultValueName)
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "ClaudePet";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return key?.GetValue(valueName) is string { Length: > 0 };
    }

    /// <summary>Registers <paramref name="executablePath"/> to start at login, or removes the registration.</summary>
    public void Set(bool enabled, string executablePath)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
            key.SetValue(valueName, $"\"{executablePath}\"");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
