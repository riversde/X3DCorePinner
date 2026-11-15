// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System;
using System.Drawing;
using Forms = System.Windows.Forms;
using X3DCorePinner.Core;

namespace X3DCorePinner.Services
{
    /// <summary>
    /// Notification-area icon with a context menu. Balloon tips are shown by Windows 10/11
    /// as standard toast notifications (and land in the Notification Center).
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly Forms.ToolStripMenuItem _toggle;
        private readonly Forms.ToolStripMenuItem _restore;

        public event Action? OpenRequested;
        public event Action? ToggleRequested;
        public event Action? RestoreRequested;
        public event Action? ExitRequested;

        public TrayService()
        {
            var menu = new Forms.ContextMenuStrip();
            var open = new Forms.ToolStripMenuItem("Open X3D Core Pinner", null, (_, _) => OpenRequested?.Invoke())
            {
                Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold)
            };
            _toggle = new Forms.ToolStripMenuItem("Pause protection", null, (_, _) => ToggleRequested?.Invoke());
            _restore = new Forms.ToolStripMenuItem("Emergency restore all apps", null, (_, _) => RestoreRequested?.Invoke());
            var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke());

            menu.Items.Add(open);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_toggle);
            menu.Items.Add(_restore);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);

            _icon = new Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = AppInfo.ProductName,
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
            };
            _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        }

        private static Icon LoadIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(AppInfo.ExePath) ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        public void SetStatus(string text, bool paused, bool supported)
        {
            // NotifyIcon.Text is limited to 127 chars (63 on older Windows).
            _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            _toggle.Text = paused ? "Resume protection" : "Pause protection";
            _toggle.Enabled = supported;
            _restore.Enabled = supported;
        }

        public void Notify(string title, string body)
        {
            try
            {
                _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Warn($"Notification failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
