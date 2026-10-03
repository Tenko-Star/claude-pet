using System.Runtime.InteropServices;
using DeskPet.App.Animation;
using DeskPet.App.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskPet.App;

/// <summary>Notification-area icon with the same scale, character, autostart and exit actions as the window menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private Drawing.Icon? _icon;
    private IntPtr _iconHandle;

    public TrayIcon(MainWindow window)
    {
        _window = window;

        var menu = new Forms.ContextMenuStrip();
        var scaleMenu = new Forms.ToolStripMenuItem("缩放");
        for (var s = WindowGeometry.MinScale; s <= WindowGeometry.MaxScale; s++)
        {
            var value = s;
            scaleMenu.DropDownItems.Add(new Forms.ToolStripMenuItem($"{value}×", null, (_, _) => window.SetScale(value)) { Tag = value });
        }
        var characterMenu = new Forms.ToolStripMenuItem("角色");
        var autoStartItem = new Forms.ToolStripMenuItem("开机自启", null, (_, _) => window.SetAutoStart(!window.IsAutoStartEnabled));
        menu.Items.Add(scaleMenu);
        menu.Items.Add(characterMenu);
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => window.RequestExit()));
        menu.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripMenuItem item in scaleMenu.DropDownItems)
            {
                item.Checked = (int)item.Tag! == window.Scale;
            }
            RebuildCharacterMenu(characterMenu);
            autoStartItem.Checked = window.IsAutoStartEnabled;
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "DeskPet",
            ContextMenuStrip = menu,
        };
        UpdateIcon();
        _notifyIcon.Visible = true;
        window.CharacterChanged += (_, _) => UpdateIcon();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        ReleaseIcon();
    }

    // The icon is the first idle frame of the current character. A separate player keeps the
    // window's animation timeline untouched.
    private void UpdateIcon()
    {
        var sprites = _window.Sprites;
        var player = new SpritePlayer(sprites.Manifest, NullLogger<SpritePlayer>.Instance);
        var frame = sprites.Compose(player.Evaluate(TimeSpan.Zero).Sprites);
        var size = Forms.SystemInformation.SmallIconSize.Width;
        IntPtr handle;
        using (var bitmap = ToBitmap(FitNearest(frame, size)))
        {
            handle = bitmap.GetHicon();
        }
        var icon = Drawing.Icon.FromHandle(handle);
        _notifyIcon.Icon = icon;
        ReleaseIcon();
        _icon = icon;
        _iconHandle = handle;
    }

    private void ReleaseIcon()
    {
        _icon?.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
        _icon = null;
        _iconHandle = IntPtr.Zero;
    }

    private void RebuildCharacterMenu(Forms.ToolStripMenuItem characterMenu)
    {
        characterMenu.DropDownItems.Clear();
        foreach (var character in _window.Characters)
        {
            var id = character.Id;
            characterMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(character.Name, null, (_, _) => _window.SwitchCharacter(id))
            {
                Checked = string.Equals(id, _window.CurrentCharacter.Id, StringComparison.OrdinalIgnoreCase),
            });
        }
        characterMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        characterMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("打开角色目录", null, (_, _) => _window.OpenCharacterFolder()));
    }

    // Pads the frame to a square and picks source pixels by nearest neighbor; never filters.
    private static PixelBuffer FitNearest(PixelBuffer source, int size)
    {
        var side = Math.Max(source.Width, source.Height);
        var offsetX = (side - source.Width) / 2;
        var offsetY = (side - source.Height) / 2;
        var result = PixelBuffer.Empty(size, size);
        for (var y = 0; y < size; y++)
        {
            var sy = y * side / size - offsetY;
            if (sy < 0 || sy >= source.Height)
            {
                continue;
            }
            for (var x = 0; x < size; x++)
            {
                var sx = x * side / size - offsetX;
                if (sx < 0 || sx >= source.Width)
                {
                    continue;
                }
                source.Pbgra.AsSpan((sy * source.Width + sx) * 4, 4).CopyTo(result.Pbgra.AsSpan((y * size + x) * 4, 4));
            }
        }
        return result;
    }

    private static Drawing.Bitmap ToBitmap(PixelBuffer buffer)
    {
        var bitmap = new Drawing.Bitmap(buffer.Width, buffer.Height, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(
            new Drawing.Rectangle(0, 0, buffer.Width, buffer.Height),
            Drawing.Imaging.ImageLockMode.WriteOnly,
            Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < buffer.Height; y++)
            {
                Marshal.Copy(buffer.Pbgra, y * buffer.Stride, data.Scan0 + y * data.Stride, buffer.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
