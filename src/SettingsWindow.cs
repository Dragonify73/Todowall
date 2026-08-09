using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    internal class SettingsWindow : Window
    {
        static SettingsWindow _instance;

        readonly Palette _p;
        bool _loading = true;

        public static void ShowSingleton()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }
            _instance = new SettingsWindow();
            _instance.Closed += delegate { _instance = null; };
            _instance.Show();
            _instance.Activate();
        }

        SettingsWindow()
        {
            _p = Core.Skin;

            Title = "TodoWall — Settings";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 780;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = true;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12.5;
            Background = new SolidColorBrush(_p.IsDark
                ? Color.FromRgb(24, 26, 32) : Color.FromRgb(246, 246, 249));
            Foreground = _p.Text;

            StackPanel root = new StackPanel();
            root.Margin = new Thickness(18, 14, 18, 16);

            // ---------------------------------------------------------- appearance
            root.Children.Add(Section("Appearance"));

            ComboBox themeCombo = Combo(new string[] { "Dark", "Light" }, Core.Config.Theme);
            themeCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                Core.Config.Theme = (string)themeCombo.SelectedItem;
                Push();
            };
            root.Children.Add(Row("Theme", themeCombo));

            root.Children.Add(Row("Accent", AccentSwatches()));

            root.Children.Add(Row("Background opacity", Slide(0.15, 1.0, Core.Config.Opacity, 0.01,
                delegate(double v) { Core.Config.Opacity = v; Push(); })));

            root.Children.Add(Row("Text size", Slide(9, 22, Core.Config.FontSize, 0.5,
                delegate(double v) { Core.Config.FontSize = v; Push(); })));

            UIElement weekHeight = Slide(120, 700, Core.Config.BarHeight, 5,
                delegate(double v) { Core.Config.BarHeight = v; PushLayout(); });
            weekHeight.SetValue(FrameworkElement.ToolTipProperty,
                "Height of one week. Showing two weeks makes the bar taller rather than "
                + "squeezing both into the same space.");
            root.Children.Add(Row("Week height", weekHeight));

            ComboBox alignCombo = Combo(new string[] { "Top", "Middle", "Bottom" }, Core.Config.VAlign);
            alignCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                Core.Config.VAlign = (string)alignCombo.SelectedItem;
                PushLayout();
            };
            root.Children.Add(Row("Vertical position", alignCombo));

            root.Children.Add(Row("Edge distance", Slide(0, 400, Core.Config.VOffset, 2,
                delegate(double v) { Core.Config.VOffset = v; PushLayout(); })));

            root.Children.Add(Row("Side margin", Slide(0, 500, Core.Config.HMargin, 2,
                delegate(double v) { Core.Config.HMargin = v; PushLayout(); })));

            root.Children.Add(Row("Corner rounding", Slide(0, 32, Core.Config.CornerRadius, 1,
                delegate(double v) { Core.Config.CornerRadius = v; Push(); })));

            // "Weeks shown" is not here on purpose: it is a view toggle, so it lives on
            // the bar's own top strip next to the settings icon.

            UIElement blurSlider = Slide(4, 60, Core.Config.BlurStrength, 1,
                delegate(double v) { Core.Config.BlurStrength = v; Push(); });

            CheckBox blur = Check("Frosted glass background (blurred wallpaper)", Core.Config.Blur);
            blur.Checked += delegate
            {
                if (_loading) return;
                Core.Config.Blur = true;
                blurSlider.IsEnabled = true;
                Push();
            };
            blur.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.Blur = false;
                blurSlider.IsEnabled = false;
                Push();
            };
            blurSlider.IsEnabled = Core.Config.Blur;
            root.Children.Add(Wrap(blur));
            root.Children.Add(Row("Blur amount", blurSlider));

            CheckBox anims = Check("Animations", Core.Config.Animations);
            anims.Checked += delegate { if (!_loading) { Core.Config.Animations = true; Push(); } };
            anims.Unchecked += delegate { if (!_loading) { Core.Config.Animations = false; Push(); } };
            root.Children.Add(Wrap(anims));

            CheckBox weekend = Check("Show Saturday and Sunday", Core.Config.ShowWeekend);
            weekend.Checked += delegate { if (!_loading) { Core.Config.ShowWeekend = true; Push(); } };
            weekend.Unchecked += delegate { if (!_loading) { Core.Config.ShowWeekend = false; Push(); } };
            root.Children.Add(Wrap(weekend));

            // ---------------------------------------------------------- clock notch
            root.Children.Add(Section("Clock"));

            CheckBox calendar = Check("Pull-down calendar (click the dash under the time)",
                Core.Config.ShowCalendar);
            calendar.IsEnabled = Core.Config.ShowClock;

            CheckBox clock = Check("Clock notch on the top edge of the screen", Core.Config.ShowClock);
            clock.Checked += delegate
            {
                if (_loading) return;
                Core.Config.ShowClock = true;
                calendar.IsEnabled = true;
                Push();
            };
            clock.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.ShowClock = false;
                calendar.IsEnabled = false;
                Push();
            };
            calendar.Checked += delegate { if (!_loading) { Core.Config.ShowCalendar = true; Push(); } };
            calendar.Unchecked += delegate { if (!_loading) { Core.Config.ShowCalendar = false; Push(); } };

            root.Children.Add(Wrap(clock));
            root.Children.Add(Indent(calendar));

            TextBlock clockNote = new TextBlock();
            clockNote.Text = "The notch takes its glass, colours, rounding and text size from the "
                           + "settings above, and shows a dot on any day that has tasks.";
            clockNote.TextWrapping = TextWrapping.Wrap;
            clockNote.Foreground = _p.Muted;
            clockNote.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(clockNote);

            // ---------------------------------------------------------- greeting
            root.Children.Add(Section("Greeting"));

            TextBox nameBox = Field(Core.Config.UserName);
            nameBox.IsEnabled = Core.Config.ShowGreeting;
            // Rebuilding the pill re-cuts its glass, which is the most expensive thing this
            // app does - so the name settles before it is applied rather than being rebuilt
            // on every keystroke.
            nameBox.TextChanged += delegate
            {
                if (_loading) return;
                Core.Config.UserName = nameBox.Text;
                NudgeGreeting();
            };

            CheckBox greeting = Check("Greeting bar under the board", Core.Config.ShowGreeting);
            greeting.Checked += delegate
            {
                if (_loading) return;
                Core.Config.ShowGreeting = true;
                nameBox.IsEnabled = true;
                Push();
            };
            greeting.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.ShowGreeting = false;
                nameBox.IsEnabled = false;
                Push();
            };
            root.Children.Add(Wrap(greeting));
            root.Children.Add(Row("Your name", nameBox));

            TextBlock greetNote = new TextBlock();
            greetNote.Text = "Good morning, afternoon or evening depending on the time of day, "
                           + "followed by your name. It takes its glass, colours and rounding from "
                           + "the settings above, and follows the bottom edge of the board.";
            greetNote.TextWrapping = TextWrapping.Wrap;
            greetNote.Foreground = _p.Muted;
            greetNote.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(greetNote);

            // ---------------------------------------------------------- behaviour
            root.Children.Add(Section("Behaviour"));

            List<string> monitors = new List<string>();
            WinForms.Screen[] screens = WinForms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                monitors.Add("Monitor " + (i + 1) + " — "
                    + screens[i].Bounds.Width + "×" + screens[i].Bounds.Height
                    + (screens[i].Primary ? " (primary)" : ""));
            }
            ComboBox monCombo = Combo(monitors.ToArray(),
                (Core.Config.Monitor >= 0 && Core.Config.Monitor < monitors.Count)
                    ? monitors[Core.Config.Monitor] : (monitors.Count > 0 ? monitors[0] : ""));
            monCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                Core.Config.Monitor = monCombo.SelectedIndex < 0 ? 0 : monCombo.SelectedIndex;
                PushLayout();
            };
            root.Children.Add(Row("Show on", monCombo));

            string[] rollLabels = { "Carry unfinished tasks over", "Start each week empty", "Copy the whole week" };
            string[] rollValues = { "CarryUnfinished", "ClearAll", "KeepAll" };
            int rollIdx = Array.IndexOf(rollValues, Core.Config.Rollover);
            if (rollIdx < 0) rollIdx = 0;
            ComboBox rollCombo = Combo(rollLabels, rollLabels[rollIdx]);
            rollCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                int i = rollCombo.SelectedIndex;
                Core.Config.Rollover = rollValues[i < 0 ? 0 : i];
                Core.Config.Save();
            };
            root.Children.Add(Row("When a new week starts", rollCombo));

            CheckBox startup = Check("Start with Windows", StartupService.IsEnabled());
            startup.Checked += delegate
            {
                if (_loading) return;
                Core.Config.StartWithWindows = true;
                StartupService.SetEnabled(true);
                Core.Config.Save();
            };
            startup.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.StartWithWindows = false;
                StartupService.SetEnabled(false);
                Core.Config.Save();
            };
            root.Children.Add(Wrap(startup));

            string[] hostLabels =
            {
                "On the desktop (editable)",
                "Welded to the desktop (editable)",
                "Behind the desktop icons (read-only)"
            };
            string[] hostValues = { "Floating", "DesktopChild", "BehindIcons" };
            int hostIdx = Array.IndexOf(hostValues, Core.Config.AttachMode);
            if (hostIdx < 0) hostIdx = 0;

            TextBlock hostNote = new TextBlock();
            hostNote.TextWrapping = TextWrapping.Wrap;
            hostNote.Foreground = _p.Muted;
            hostNote.Margin = new Thickness(0, 4, 0, 0);
            hostNote.Text = HostNote(hostIdx);

            ComboBox hostCombo = Combo(hostLabels, hostLabels[hostIdx]);
            hostCombo.SelectionChanged += delegate
            {
                int i = hostCombo.SelectedIndex < 0 ? 0 : hostCombo.SelectedIndex;
                hostNote.Text = HostNote(i);
                if (_loading) return;
                Core.Config.AttachMode = hostValues[i];
                PushLayout();
            };
            root.Children.Add(Row("Placement", hostCombo));
            root.Children.Add(hostNote);

            // ---------------------------------------------------------- remove
            root.Children.Add(Section("Remove"));

            TextBlock removeNote = new TextBlock();
            removeNote.Text = "TodoWall has no installer — it is one file that keeps its data in "
                            + "%APPDATA%\\TodoWall. Uninstalling turns off start-with-Windows, then "
                            + "deletes that data (it asks first) and the program file itself.";
            removeNote.TextWrapping = TextWrapping.Wrap;
            removeNote.Foreground = _p.Muted;
            removeNote.Margin = new Thickness(0, 0, 0, 8);
            root.Children.Add(removeNote);

            Button uninstall = Btn("Uninstall TodoWall…");
            uninstall.HorizontalAlignment = HorizontalAlignment.Left;
            uninstall.MinWidth = 150;
            uninstall.Click += delegate { Uninstaller.Prompt(this); };
            root.Children.Add(uninstall);

            // ---------------------------------------------------------- footer
            TextBlock help = new TextBlock();
            help.Text = "Click a circle to tick a task · click the text to edit it · Enter saves and "
                      + "opens the next one · right-click a task to move or delete it.";
            help.TextWrapping = TextWrapping.Wrap;
            help.Foreground = _p.Muted;
            help.Margin = new Thickness(0, 14, 0, 8);
            root.Children.Add(help);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            Button reset = Btn("Reset layout");
            reset.Margin = new Thickness(0, 0, 8, 0);
            reset.Click += delegate
            {
                Settings d = new Settings();
                Core.Config.Opacity = d.Opacity;
                Core.Config.FontSize = d.FontSize;
                Core.Config.BarHeight = d.BarHeight;
                Core.Config.VAlign = d.VAlign;
                Core.Config.VOffset = d.VOffset;
                Core.Config.HMargin = d.HMargin;
                Core.Config.CornerRadius = d.CornerRadius;
                Core.Config.WeekRows = d.WeekRows;
                Core.Config.Blur = d.Blur;
                Core.Config.BlurStrength = d.BlurStrength;
                Core.Config.Animations = d.Animations;
                Core.Config.Accent = d.Accent;
                Push();
                Close();
                ShowSingleton();
            };
            Button close = Btn("Done");
            close.Click += delegate { Close(); };

            buttons.Children.Add(reset);
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = root;
            Content = sv;

            _loading = false;
        }

        // ------------------------------------------------------------- live apply

        void Push()
        {
            Core.Config.Save();
            if (Core.Wall != null) Core.Wall.ApplySettingsChanged();
            Core.SyncClock();
            // After the board, always: the pill is placed against the board's edge, so it
            // has to be told once the board has finished moving.
            Core.SyncGreeting();
            if (Core.TrayIcon != null) Core.TrayIcon.RefreshIcon();
        }

        DispatcherTimer _nameSettle;

        /// <summary>Apply the name once the typing stops.</summary>
        void NudgeGreeting()
        {
            if (_nameSettle == null)
            {
                _nameSettle = new DispatcherTimer();
                _nameSettle.Interval = TimeSpan.FromMilliseconds(450);
                _nameSettle.Tick += delegate
                {
                    _nameSettle.Stop();
                    Core.Config.Save();
                    Core.SyncGreeting();
                };
            }
            _nameSettle.Stop();
            _nameSettle.Start();
        }

        void PushLayout()
        {
            Core.Config.Save();
            if (Core.Wall != null)
            {
                Core.Wall.Attach(false);
                Core.Wall.Relayout();
            }
            if (Core.Clock != null)
            {
                Core.Clock.Attach(false);
                Core.Clock.Relayout();
            }
            if (Core.Greeting != null)
            {
                Core.Greeting.Attach(false);
                Core.Greeting.Relayout();
            }
        }

        static string HostNote(int index)
        {
            if (index == 1)
                return "Reparented into Explorer's desktop. Sticks to the desktop more firmly, "
                     + "but relies on Explorer's internals and can break when Explorer restarts. "
                     + "Falls back to the first option if it fails.";
            if (index == 2)
                return "Genuinely part of the wallpaper, under your icons — but Windows sends every "
                     + "click to the icon layer, so tasks can't be edited in this mode.";
            return "A window pinned to the bottom of the stack: above the wallpaper, below every "
                 + "real window, and clickable. Draws over any desktop icons it overlaps.";
        }

        // ------------------------------------------------------------- builders

        UIElement Section(string title)
        {
            TextBlock t = new TextBlock();
            t.Text = title.ToUpperInvariant();
            t.FontWeight = FontWeights.Bold;
            t.FontSize = 10.5;
            t.Foreground = _p.Accent;
            t.Margin = new Thickness(0, 16, 0, 8);
            return t;
        }

        UIElement Wrap(UIElement inner)
        {
            Border b = new Border();
            b.Child = inner;
            b.Margin = new Thickness(0, 3, 0, 3);
            return b;
        }

        /// <summary>A checkbox that only means anything while the one above it is ticked.</summary>
        UIElement Indent(UIElement inner)
        {
            Border b = new Border();
            b.Child = inner;
            b.Margin = new Thickness(22, 3, 0, 3);
            return b;
        }

        UIElement Row(string label, UIElement control)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 3, 0, 3);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock t = new TextBlock();
            t.Text = label;
            t.Foreground = _p.Text;
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 0);
            g.Children.Add(t);

            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        ComboBox Combo(string[] items, string selected)
        {
            ComboBox c = new ComboBox();
            foreach (string s in items) c.Items.Add(s);
            c.SelectedItem = selected;
            if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
            c.VerticalAlignment = VerticalAlignment.Center;
            return c;
        }

        TextBox Field(string value)
        {
            TextBox t = new TextBox();
            t.Text = value == null ? "" : value;
            t.MaxLength = 24;
            t.Padding = new Thickness(4, 3, 4, 3);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        CheckBox Check(string text, bool value)
        {
            CheckBox c = new CheckBox();
            c.Content = text;
            c.IsChecked = value;
            c.Foreground = _p.Text;
            c.VerticalContentAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(0, 4, 0, 4);
            return c;
        }

        Button Btn(string text)
        {
            Button b = new Button();
            b.Content = text;
            b.Padding = new Thickness(12, 5, 12, 5);
            b.MinWidth = 84;
            return b;
        }

        UIElement Slide(double min, double max, double value, double tick, Action<double> onChange)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            Slider s = new Slider();
            s.Minimum = min;
            s.Maximum = max;
            s.Value = Math.Max(min, Math.Min(max, value));
            s.SmallChange = tick;
            s.LargeChange = tick * 5;
            s.IsSnapToTickEnabled = false;
            s.VerticalAlignment = VerticalAlignment.Center;

            TextBlock read = new TextBlock();
            read.Foreground = _p.Muted;
            read.VerticalAlignment = VerticalAlignment.Center;
            read.TextAlignment = TextAlignment.Right;
            read.Text = Fmt(s.Value, max);

            s.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e)
            {
                double v = Math.Round(e.NewValue / tick) * tick;
                read.Text = Fmt(v, max);
                if (_loading) return;
                onChange(v);
            };

            Grid.SetColumn(s, 0);
            Grid.SetColumn(read, 1);
            g.Children.Add(s);
            g.Children.Add(read);
            return g;
        }

        static string Fmt(double v, double max)
        {
            if (max <= 1.01) return Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%";
            if (v == Math.Floor(v)) return ((int)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        UIElement AccentSwatches()
        {
            string[] colors =
            {
                "#FF6FB1FF", "#FF7EE7C4", "#FFFFC46B", "#FFFF8A9B",
                "#FFC79BFF", "#FF9BE36D", "#FFFF9F5A", "#FFE6E9F0"
            };

            WrapPanel wrap = new WrapPanel();
            wrap.VerticalAlignment = VerticalAlignment.Center;

            foreach (string hex in colors)
            {
                string h = hex;
                Border dot = new Border();
                dot.Width = 22;
                dot.Height = 22;
                dot.CornerRadius = new CornerRadius(11);
                dot.Margin = new Thickness(0, 0, 6, 0);
                dot.Background = new SolidColorBrush(Palette.Parse(h, Colors.SkyBlue));
                dot.Cursor = Cursors.Hand;
                dot.BorderThickness = new Thickness(2);
                dot.BorderBrush = string.Equals(h, Core.Config.Accent, StringComparison.OrdinalIgnoreCase)
                    ? _p.Text : Brushes.Transparent;
                dot.MouseLeftButtonUp += delegate
                {
                    Core.Config.Accent = h;
                    foreach (object child in wrap.Children)
                    {
                        Border b = child as Border;
                        if (b != null) b.BorderBrush = Brushes.Transparent;
                    }
                    dot.BorderBrush = _p.Text;
                    Push();
                };
                wrap.Children.Add(dot);
            }
            return wrap;
        }
    }
}
