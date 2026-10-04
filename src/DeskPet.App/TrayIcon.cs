using DeskPet.App.Windowing;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskPet.App;

/// <summary>
/// Notification-area icon with the same scale, character, autostart and exit actions as the window menu.
/// A left click brings the pet back when it got lost.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _icon;

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

        _icon = LoadAppIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "DeskPet",
            ContextMenuStrip = menu,
            Icon = _icon,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                window.BringBack();
            }
        };
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    // The application icon embedded by the project file; the size closest to the small icon size is picked.
    private static Drawing.Icon LoadAppIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("DeskPet.App.app.ico")
            ?? throw new InvalidOperationException("Embedded resource DeskPet.App.app.ico is missing.");
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
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
}
