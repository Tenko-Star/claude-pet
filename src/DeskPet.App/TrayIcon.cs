using System.Runtime.InteropServices;
using DeskPet.App.Animation;
using DeskPet.App.Rendering;
using DeskPet.App.Windowing;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskPet.App;

/// <summary>Notification-area icon with the same scale, talk and exit actions as the window menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _icon;
    private readonly IntPtr _iconHandle;

    public TrayIcon(MainWindow window, SpriteLibrary sprites, SpritePlayer player)
    {
        // The icon is the first frame of the animation, as the manifest defines it.
        var frame = sprites.Compose(player.Evaluate(TimeSpan.Zero).Files);
        var size = Forms.SystemInformation.SmallIconSize.Width;
        using (var bitmap = ToBitmap(FitNearest(frame, size)))
        {
            _iconHandle = bitmap.GetHicon();
        }
        _icon = Drawing.Icon.FromHandle(_iconHandle);

        var menu = new Forms.ContextMenuStrip();
        var scaleMenu = new Forms.ToolStripMenuItem("缩放");
        for (var s = WindowGeometry.MinScale; s <= WindowGeometry.MaxScale; s++)
        {
            var value = s;
            scaleMenu.DropDownItems.Add(new Forms.ToolStripMenuItem($"{value}×", null, (_, _) => window.SetScale(value)) { Tag = value });
        }
        var talkItem = new Forms.ToolStripMenuItem("说话", null, (_, _) => window.SetTalking(!window.IsTalking));
        menu.Items.Add(scaleMenu);
        menu.Items.Add(talkItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => window.RequestExit()));
        menu.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripMenuItem item in scaleMenu.DropDownItems)
            {
                item.Checked = (int)item.Tag! == window.Scale;
            }
            talkItem.Checked = window.IsTalking;
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "DeskPet",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
        DestroyIcon(_iconHandle);
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
