using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>Notification-area icon: the only always-available way back into the app,
    /// since the bar itself never appears in the taskbar or Alt+Tab.</summary>
    internal class Tray : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        readonly WinForms.NotifyIcon _icon;
        IntPtr _iconHandle = IntPtr.Zero;
        bool _barVisible = true;

        public Tray()
        {
            _icon = new WinForms.NotifyIcon();
            _icon.Text = "TodoWall";
            _icon.Icon = BuildIcon(Palette.Parse(Core.Config.Accent, System.Windows.Media.Color.FromArgb(255, 111, 177, 255)));
            _icon.Visible = true;

            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();

            WinForms.ToolStripMenuItem toggle = new WinForms.ToolStripMenuItem("Hide the bar");
            toggle.Click += delegate
            {
                _barVisible = !_barVisible;
                if (Core.Wall != null)
                {
                    Core.Wall.Visibility = _barVisible ? Visibility.Visible : Visibility.Hidden;
                    if (_barVisible) Core.Wall.Attach(true);
                }
                // The clock is part of the same widget: leaving a notch stranded on an
                // otherwise cleared desktop is not what "hide" means.
                if (Core.Clock != null)
                {
                    if (!_barVisible) Core.Clock.CloseCalendar();
                    Core.Clock.Visibility = _barVisible ? Visibility.Visible : Visibility.Hidden;
                    if (_barVisible) Core.Clock.Attach(true);
                }
                if (Core.Greeting != null)
                {
                    Core.Greeting.Visibility = _barVisible ? Visibility.Visible : Visibility.Hidden;
                    if (_barVisible) Core.Greeting.Attach(true);
                }
                toggle.Text = _barVisible ? "Hide the bar" : "Show the bar";
            };

            menu.Items.Add("Settings…", null, delegate { OnUi(delegate { SettingsWindow.ShowSingleton(); }); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(toggle);
            menu.Items.Add("Re-attach to desktop", null, delegate
            {
                OnUi(delegate { if (Core.Wall != null) Core.Wall.Attach(true); });
            });
            menu.Items.Add("Bring to front (troubleshoot)", null, delegate
            {
                OnUi(delegate { if (Core.Wall != null) Core.Wall.BringToFrontForDebug(); });
            });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate
            {
                OnUi(delegate
                {
                    Core.SaveBoardNow();
                    System.Windows.Application.Current.Shutdown();
                });
            });

            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { OnUi(delegate { SettingsWindow.ShowSingleton(); }); };
        }

        static void OnUi(Action a)
        {
            System.Windows.Application app = System.Windows.Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
            else a();
        }

        public void RefreshIcon()
        {
            try
            {
                Icon old = _icon.Icon;
                IntPtr oldHandle = _iconHandle;
                _icon.Icon = BuildIcon(Palette.Parse(Core.Config.Accent, System.Windows.Media.Color.FromArgb(255, 111, 177, 255)));
                if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
                if (old != null) old.Dispose();
            }
            catch { }
        }

        Icon BuildIcon(System.Windows.Media.Color accent)
        {
            Color c = Color.FromArgb(255, accent.R, accent.G, accent.B);
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(c))
                        g.FillEllipse(b, 1, 1, 30, 30);
                    using (Pen pen = new Pen(Color.FromArgb(255, 20, 22, 28), 3.4f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(9f, 16.5f), new PointF(14f, 21.5f), new PointF(23f, 10.5f)
                        });
                    }
                }
                _iconHandle = bmp.GetHicon();
                // Clone so the icon survives the source bitmap being disposed.
                using (Icon tmp = Icon.FromHandle(_iconHandle))
                    return (Icon)tmp.Clone();
            }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                if (_icon.Icon != null) _icon.Icon.Dispose();
                _icon.Dispose();
                if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
            }
            catch { }
        }
    }
}
