using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>
    /// Settings, built as a flyout rather than a dialog.
    ///
    /// It is the same card the right-click menus are: a rounded, hairline-bordered panel
    /// with a soft shadow, frosted with the same slice of blurred wallpaper, rows that
    /// light up in the accent colour, and Fluent glyphs down the left. The window itself
    /// is chromeless - a Windows title bar in the middle of that would be the one part of
    /// the app that looked like a different program - so the card carries its own header,
    /// and dragging it moves the window.
    ///
    /// Every colour, metric and control template lives in one generated dictionary hung on
    /// the window, so changing theme or accent is a matter of rebuilding that dictionary:
    /// the tree stays up, nothing is reconstructed, and the panel restyles under the
    /// pointer. Only the glass has to be re-cut, and that is debounced - a slider drag
    /// would otherwise ask for a fresh wallpaper blur on every frame.
    /// </summary>
    internal class SettingsWindow : Window
    {
        // Room left inside the window for the card's shadow to spill into - the same idea
        // as MenuChrome's gutter, and what FrostCard subtracts back off to find the card.
        const int Gutter = 16;
        const int Radius = 12;
        const int CardWidth = 470;
        const int ControlColumn = 238;   // every control starts on the same line

        static SettingsWindow _instance;

        Border _chrome;                 // the card: border, shadow, glass
        Border _tint;                   // the wash over the glass that keeps text readable
        bool _loading = true;

        DispatcherTimer _nameSettle;    // the greeting name, applied once typing stops
        DispatcherTimer _frostSettle;   // the glass, re-cut once dragging stops
        string _frostKey = "";

        // Rows that only mean anything while the toggle above them is on.
        UIElement _blurRow, _calendarRow, _nameRow, _formatRow;
        UIElement _watchRow, _everyRow;

        // Accent swatches: the tick moves between them, so they are kept to be updated.
        readonly List<KeyValuePair<string, TextBlock>> _swatches =
            new List<KeyValuePair<string, TextBlock>>();

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
            Title = "TodoWall — Settings";
            WindowStyle = WindowStyle.None;         // the card is the chrome
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = CardWidth + Gutter * 2;
            MaxHeight = Math.Max(460, Math.Min(900, SystemParameters.WorkArea.Height - 24));
            ShowInTaskbar = true;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            Resources = BuildStyles(Core.Skin);
            FontSize = BodySize();

            Content = BuildCard();

            Loaded += delegate
            {
                CentreOnScreen();
                Frost(true);
            };
            LocationChanged += delegate { FrostSoon(); };
            SizeChanged += delegate { FrostSoon(); };

            _loading = false;
        }

        // ===================================================================== the card

        UIElement BuildCard()
        {
            _chrome = new Border();
            _chrome.Margin = new Thickness(Gutter);
            _chrome.CornerRadius = new CornerRadius(Radius);
            _chrome.BorderThickness = new Thickness(1);
            _chrome.SnapsToDevicePixels = true;
            _chrome.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            _chrome.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 20,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.38
            };
            // The glass is a bitmap stretched to the card; it never needs resampling
            // quality, and this window is composited in software.
            RenderOptions.SetBitmapScalingMode(_chrome, BitmapScalingMode.LowQuality);

            _tint = new Border();
            _tint.CornerRadius = new CornerRadius(Radius);

            DockPanel dock = new DockPanel();
            dock.LastChildFill = true;

            UIElement header = Header();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            UIElement footer = Footer();
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Focusable = false;
            sv.Padding = new Thickness(14, 0, 8, 4);
            sv.Content = Body();
            dock.Children.Add(sv);

            Grid stack = new Grid();
            stack.Children.Add(_tint);
            stack.Children.Add(dock);
            _chrome.Child = stack;
            return _chrome;
        }

        UIElement Header()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(18, 13, 12, 4);
            g.Background = Brushes.Transparent;      // so the whole strip is draggable
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = Styled(new TextBlock(), "Glyph");
            icon.Text = MenuChrome.GlyphSettings;
            icon.FontSize = 17;
            icon.Width = 26;
            Grid.SetColumn(icon, 0);

            TextBlock title = Styled(new TextBlock(), "Head");
            title.Text = "Settings";
            Grid.SetColumn(title, 1);

            Button close = new Button();
            close.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            close.Content = MenuChrome.GlyphClose;
            close.ToolTip = "Close";
            close.Click += delegate { Close(); };
            Grid.SetColumn(close, 2);

            g.Children.Add(icon);
            g.Children.Add(title);
            g.Children.Add(close);

            g.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); }
                catch { }   // DragMove throws if the button was already released
            };
            return g;
        }

        UIElement Body()
        {
            StackPanel root = new StackPanel();

            // ------------------------------------------------------------- start-up
            // First thing in the panel, and its own card: it is the one setting that
            // decides whether any of the others are ever seen.
            CheckBox startup = Switch(StartupService.IsEnabled());
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
            root.Children.Add(Feature(MenuChrome.GlyphExit, "Start with Windows",
                "The board is on the desktop the moment you sign in.", startup));

            // ------------------------------------------------------------ appearance
            root.Children.Add(Section(MenuChrome.GlyphColor, "Appearance"));

            ComboBox themeCombo = Combo(new string[] { "Dark", "Light" }, Core.Config.Theme);
            themeCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                Core.Config.Theme = (string)themeCombo.SelectedItem;
                Push();
                Restyle();
            };
            root.Children.Add(Row("Theme", themeCombo));

            root.Children.Add(Row("Accent", AccentSwatches()));

            root.Children.Add(Row("Background opacity", Slide(0.15, 1.0, Core.Config.Opacity, 0.01,
                delegate(double v) { Core.Config.Opacity = v; Push(); })));

            root.Children.Add(Row("Text size", Slide(9, 22, Core.Config.FontSize, 0.5,
                delegate(double v) { Core.Config.FontSize = v; Push(); })));

            UIElement weekHeight = Slide(120, 700, Core.Config.BarHeight, 5,
                delegate(double v) { Core.Config.BarHeight = v; PushLayout(); });
            UIElement weekRow = Row("Week height", weekHeight);
            weekRow.SetValue(FrameworkElement.ToolTipProperty,
                "Height of one week. Showing two weeks makes the bar taller rather than "
                + "squeezing both into the same space.");
            root.Children.Add(weekRow);

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

            _blurRow = Row("Blur amount", Slide(4, 60, Core.Config.BlurStrength, 1,
                delegate(double v) { Core.Config.BlurStrength = v; Push(); }));

            CheckBox blur = Switch(Core.Config.Blur);
            blur.Checked += delegate
            {
                if (_loading) return;
                Core.Config.Blur = true;
                Enable(_blurRow, true);
                SyncWatchRows();
                Push();
            };
            blur.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.Blur = false;
                Enable(_blurRow, false);
                SyncWatchRows();
                Push();
            };
            Enable(_blurRow, Core.Config.Blur);
            root.Children.Add(Row("Frosted glass", blur, "The wallpaper, blurred, behind the board."));
            root.Children.Add(_blurRow);

            // The wallpaper poll. It belongs here rather than under Behaviour because the
            // only thing it affects is the glass above it - and it is meaningless without
            // it, which is why both rows follow the frosted-glass toggle as well.
            string[] everyLabels = { "Every minute", "Every 5 minutes", "Every 10 minutes" };
            int[] everyValues = { 1, 5, 10 };
            int everyIdx = Array.IndexOf(everyValues, Core.Config.WatchMinutes);
            if (everyIdx < 0) everyIdx = 1;
            ComboBox everyCombo = Combo(everyLabels, everyLabels[everyIdx]);
            everyCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                int i = everyCombo.SelectedIndex;
                Core.Config.WatchMinutes = everyValues[i < 0 ? 1 : i];
                Core.Config.Save();
                WallpaperWatch.Sync();
            };
            _everyRow = Row("Check every", everyCombo);

            CheckBox watch = Switch(Core.Config.WatchWallpaper);
            watch.Checked += delegate
            {
                if (_loading) return;
                Core.Config.WatchWallpaper = true;
                Core.Config.Save();
                SyncWatchRows();
                WallpaperWatch.Sync();
            };
            watch.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.WatchWallpaper = false;
                Core.Config.Save();
                SyncWatchRows();
                WallpaperWatch.Sync();
            };
            _watchRow = Row("Wallpaper changes", watch,
                "Catches a slideshow or Spotlight swapping the picture.");
            root.Children.Add(_watchRow);
            root.Children.Add(_everyRow);
            SyncWatchRows();

            CheckBox anims = Switch(Core.Config.Animations);
            anims.Checked += delegate { if (!_loading) { Core.Config.Animations = true; Push(); } };
            anims.Unchecked += delegate { if (!_loading) { Core.Config.Animations = false; Push(); } };
            root.Children.Add(Row("Animations", anims));

            CheckBox weekend = Switch(Core.Config.ShowWeekend);
            weekend.Checked += delegate { if (!_loading) { Core.Config.ShowWeekend = true; Push(); } };
            weekend.Unchecked += delegate { if (!_loading) { Core.Config.ShowWeekend = false; Push(); } };
            root.Children.Add(Row("Saturday and Sunday", weekend));

            // ------------------------------------------------------------ clock notch
            root.Children.Add(Section(MenuChrome.GlyphClock, "Clock"));

            CheckBox calendar = Switch(Core.Config.ShowCalendar);
            calendar.Checked += delegate { if (!_loading) { Core.Config.ShowCalendar = true; Push(); } };
            calendar.Unchecked += delegate { if (!_loading) { Core.Config.ShowCalendar = false; Push(); } };
            _calendarRow = Row("Pull-down calendar", calendar,
                "Click the dash under the time. A dot marks any day with tasks.");

            // 24-hour first, because it is the default and the one the face is designed
            // around; 12-hour sets the meridiem smaller than the digits beside it.
            string[] formatLabels = { "24-hour", "12-hour, with AM/PM" };
            ComboBox formatCombo = Combo(formatLabels, formatLabels[Core.Config.Clock24Hour ? 0 : 1]);
            formatCombo.SelectionChanged += delegate
            {
                if (_loading) return;
                Core.Config.Clock24Hour = formatCombo.SelectedIndex != 1;
                Push();
            };
            _formatRow = Row("Time format", formatCombo);

            CheckBox clock = Switch(Core.Config.ShowClock);
            clock.Checked += delegate
            {
                if (_loading) return;
                Core.Config.ShowClock = true;
                Enable(_calendarRow, true);
                Enable(_formatRow, true);
                Push();
            };
            clock.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.ShowClock = false;
                Enable(_calendarRow, false);
                Enable(_formatRow, false);
                Push();
            };
            Enable(_calendarRow, Core.Config.ShowClock);
            Enable(_formatRow, Core.Config.ShowClock);

            root.Children.Add(Row("Notch on the top edge", clock,
                "Takes its glass, colours, rounding and text size from Appearance."));
            root.Children.Add(_formatRow);
            root.Children.Add(_calendarRow);

            // -------------------------------------------------------------- greeting
            root.Children.Add(Section(MenuChrome.GlyphPerson, "Greeting"));

            // Rebuilding the pill re-cuts its glass, which is the most expensive thing this
            // app does - so the name settles before it is applied rather than being rebuilt
            // on every keystroke.
            TextBox nameBox = Field(Core.Config.UserName);
            nameBox.TextChanged += delegate
            {
                if (_loading) return;
                Core.Config.UserName = nameBox.Text;
                NudgeGreeting();
            };
            _nameRow = Row("Your name", nameBox);

            // The name is said by both the pill and the welcome screen, so the field stays
            // live while either of them is on.
            CheckBox greeting = Switch(Core.Config.ShowGreeting);
            greeting.Checked += delegate
            {
                if (_loading) return;
                Core.Config.ShowGreeting = true;
                Enable(_nameRow, true);
                Push();
            };
            greeting.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.ShowGreeting = false;
                Enable(_nameRow, Core.Config.ShowWelcome);
                Push();
            };

            // The welcome screen is only ever shown at start-up and on unlock, so flipping
            // this changes nothing on screen now: it is saved, and that is all. Nothing to push.
            CheckBox welcome = Switch(Core.Config.ShowWelcome);
            welcome.Checked += delegate
            {
                if (_loading) return;
                Core.Config.ShowWelcome = true;
                Enable(_nameRow, true);
                Core.Config.Save();
            };
            welcome.Unchecked += delegate
            {
                if (_loading) return;
                Core.Config.ShowWelcome = false;
                Enable(_nameRow, Core.Config.ShowGreeting);
                Core.Config.Save();
            };
            Enable(_nameRow, Core.Config.ShowGreeting || Core.Config.ShowWelcome);

            // Otherwise the only way to see what the switch above does is to log out.
            Button preview = Btn("Show it now", null);
            preview.HorizontalAlignment = HorizontalAlignment.Left;
            preview.Click += delegate
            {
                // Apply a name that is still settling first, so the preview says it.
                if (_nameSettle != null && _nameSettle.IsEnabled)
                {
                    _nameSettle.Stop();
                    Core.Config.Save();
                    Core.SyncGreeting();
                }
                WelcomeWindow.ShowNow();
            };

            root.Children.Add(Row("Bar under the board", greeting,
                "Good morning, afternoon or evening, then your name."));
            root.Children.Add(Row("Welcome screen at sign-in", welcome,
                "\"Welcome back\" and a line for the day, over the whole screen, when TodoWall "
                + "starts and every time you unlock the PC - with start-with-Windows on, that "
                + "is every sign-in. A click takes it away."));
            root.Children.Add(Row("Preview the welcome screen", preview));
            root.Children.Add(_nameRow);

            // ------------------------------------------------------------- behaviour
            root.Children.Add(Section(MenuChrome.GlyphMove, "Behaviour"));

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

            // The board lives on the desktop, full stop: it is the only placement that is
            // both clickable and independent of Explorer's internals, so it is no longer
            // offered as a choice. See DesktopHost.Mode.

            // ---------------------------------------------------------------- remove
            root.Children.Add(Section(MenuChrome.GlyphDelete, "Remove"));

            TextBlock removeNote = Styled(new TextBlock(), "Note");
            removeNote.Text = "TodoWall has no installer — it is one file that keeps its data in "
                            + "%APPDATA%\\TodoWall. Uninstalling turns off start-with-Windows, then "
                            + "deletes that data (it asks first) and the program file itself.";
            removeNote.Margin = new Thickness(10, 2, 10, 8);
            root.Children.Add(removeNote);

            Button uninstall = Btn("Uninstall TodoWall…", null);
            uninstall.HorizontalAlignment = HorizontalAlignment.Left;
            uninstall.Margin = new Thickness(10, 0, 0, 4);
            uninstall.Click += delegate { Uninstaller.Prompt(this); };
            root.Children.Add(uninstall);

            return root;
        }

        UIElement Footer()
        {
            StackPanel foot = new StackPanel();
            foot.Margin = new Thickness(18, 6, 18, 15);

            Border rule = new Border();
            rule.Height = 1;
            rule.Margin = new Thickness(0, 0, 0, 11);
            rule.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            foot.Children.Add(rule);

            TextBlock help = Styled(new TextBlock(), "Note");
            help.Text = "Click a circle to tick a task · click the text to edit it · Enter saves and "
                      + "opens the next one · right-click a task to move or delete it.";
            help.Margin = new Thickness(0, 0, 0, 11);
            foot.Children.Add(help);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            Button reset = Btn("Reset layout", null);
            reset.Margin = new Thickness(0, 0, 8, 0);
            reset.Click += delegate { ResetLayout(); };

            Button done = Btn("Done", "AccentButton");
            done.Click += delegate { Close(); };

            buttons.Children.Add(reset);
            buttons.Children.Add(done);
            foot.Children.Add(buttons);
            return foot;
        }

        /// <summary>Put the look back to the defaults, in place: everything the panel shows
        /// is bound to nothing, so the tree is rebuilt rather than each control poked.</summary>
        void ResetLayout()
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
        }

        // =================================================================== placement

        /// <summary>Centre on the monitor the board is on, in device pixels.
        ///
        /// Window.Left/Top are DIPs against the primary monitor's scaling, which is the
        /// wrong ruler the moment a second screen runs at a different DPI - and this app
        /// already places all of its windows through SetWindowPos for exactly that reason.</summary>
        void CentreOnScreen()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return;
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;

                WinForms.Screen[] all = WinForms.Screen.AllScreens;
                int i = Core.Config.Monitor;
                System.Drawing.Rectangle wa = (i >= 0 && i < all.Length ? all[i] : WinForms.Screen.PrimaryScreen)
                    .WorkingArea;

                Native.SetWindowPos(h, IntPtr.Zero,
                    wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - ht) / 2, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            catch (Exception ex) { Log.Write("settings placement failed: " + ex.Message); }
        }

        // ======================================================================= glass

        /// <summary>Re-cut the wallpaper behind the card. Skipped when nothing that the cut
        /// depends on has moved, since it decodes and blurs an image every time.</summary>
        void Frost(bool force)
        {
            if (!IsLoaded) return;
            string key = string.Join("|", new string[]
            {
                BlurBackdrop.SourceKey(), Core.Config.Theme,
                Math.Round(Left).ToString(CultureInfo.InvariantCulture),
                Math.Round(Top).ToString(CultureInfo.InvariantCulture),
                Math.Round(ActualWidth).ToString(CultureInfo.InvariantCulture),
                Math.Round(ActualHeight).ToString(CultureInfo.InvariantCulture),
                Core.Config.Blur ? "1" : "0",
                Core.Config.Opacity.ToString("0.00", CultureInfo.InvariantCulture),
                Core.Config.BlurStrength.ToString("0.0", CultureInfo.InvariantCulture)
            });
            if (!force && key == _frostKey) return;
            _frostKey = key;
            MenuChrome.FrostCard(_chrome, _tint, new Thickness(Gutter));
        }

        /// <summary>Re-cut once the drag, or the drag of a slider, stops.</summary>
        void FrostSoon()
        {
            if (_frostSettle == null)
            {
                _frostSettle = new DispatcherTimer();
                _frostSettle.Interval = TimeSpan.FromMilliseconds(160);
                _frostSettle.Tick += delegate
                {
                    _frostSettle.Stop();
                    Frost(false);
                };
            }
            _frostSettle.Stop();
            _frostSettle.Start();
        }

        // ================================================================= live apply

        void Push()
        {
            Core.Config.Save();
            if (Core.Wall != null) Core.Wall.ApplySettingsChanged();
            else Core.RebuildSkin();
            Core.SyncClock();
            // After the board, always: the pill is placed against the board's edge, so it
            // has to be told once the board has finished moving.
            Core.SyncGreeting();
            if (Core.TrayIcon != null) Core.TrayIcon.RefreshIcon();
            // Cheap and idempotent: it only does anything when the poll's own settings, or
            // the frosted glass it exists to refresh, actually moved.
            WallpaperWatch.Sync();
            FrostSoon();
        }

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

        /// <summary>Re-skin the panel in place after a theme or accent change. Replacing the
        /// dictionary invalidates every style and resource reference below the window, so
        /// the whole card re-colours without a single control being rebuilt.</summary>
        void Restyle()
        {
            Resources = BuildStyles(Core.Skin);
            FontSize = BodySize();
            MarkAccent();
            Frost(true);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Dispatcher timers are rooted by the dispatcher, not by this window: one left
            // running would keep the whole panel - and its glass bitmap - alive for the
            // lifetime of the app.
            if (_nameSettle != null) { _nameSettle.Stop(); _nameSettle = null; }
            if (_frostSettle != null) { _frostSettle.Stop(); _frostSettle = null; }
            _chrome = null;
            _tint = null;
            base.OnClosed(e);
        }

        // ==================================================================== builders

        static double BodySize()
        {
            double v = Core.Config.FontSize * 0.95;
            return v < 12.5 ? 12.5 : (v > 16 ? 16 : v);
        }

        static T Styled<T>(T element, string key) where T : FrameworkElement
        {
            element.SetResourceReference(FrameworkElement.StyleProperty, key);
            return element;
        }

        /// <summary>A section heading: glyph, name, and a hairline running out to the edge.</summary>
        UIElement Section(string glyph, string title)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(10, 18, 10, 7);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock icon = Styled(new TextBlock(), "Glyph");
            icon.Text = glyph;
            icon.FontSize = 13;
            icon.Width = 20;
            Grid.SetColumn(icon, 0);

            TextBlock t = Styled(new TextBlock(), "Section");
            t.Text = title.ToUpperInvariant();
            Grid.SetColumn(t, 1);

            Border rule = new Border();
            rule.Height = 1;
            rule.VerticalAlignment = VerticalAlignment.Center;
            rule.Margin = new Thickness(10, 1, 0, 0);
            rule.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            Grid.SetColumn(rule, 2);

            g.Children.Add(icon);
            g.Children.Add(t);
            g.Children.Add(rule);
            return g;
        }

        UIElement Row(string label, UIElement control)
        {
            return Row(label, control, null);
        }

        /// <summary>Label on the left, control on the right, the whole strip highlighting
        /// under the pointer - the same row a menu item is.</summary>
        UIElement Row(string label, UIElement control, string note)
        {
            Grid g = new Grid();
            // The label takes what is left rather than a fixed slice: a long one wraps
            // instead of being cut off, and the controls still line up with each other.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ControlColumn) });

            StackPanel left = new StackPanel();
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Margin = new Thickness(0, 0, 12, 0);

            TextBlock t = Styled(new TextBlock(), "Label");
            t.Text = label;
            left.Children.Add(t);

            if (!string.IsNullOrEmpty(note))
            {
                TextBlock n = Styled(new TextBlock(), "Note");
                n.Text = note;
                n.Margin = new Thickness(0, 2, 0, 0);
                left.Children.Add(n);
            }

            Grid.SetColumn(left, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(left);
            g.Children.Add(control);

            Border host = Styled(new Border(), "Row");
            host.Child = g;
            return host;
        }

        /// <summary>The one setting that gets a card of its own, at the top of the panel.</summary>
        UIElement Feature(string glyph, string label, string note, UIElement control)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = Styled(new TextBlock(), "Glyph");
            icon.Text = glyph;
            icon.FontSize = 17;
            icon.Width = 28;
            Grid.SetColumn(icon, 0);

            StackPanel text = new StackPanel();
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 12, 0);

            TextBlock t = Styled(new TextBlock(), "Label");
            t.Text = label;
            text.Children.Add(t);

            TextBlock n = Styled(new TextBlock(), "Note");
            n.Text = note;
            n.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(n);
            Grid.SetColumn(text, 1);

            control.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            Grid.SetColumn(control, 2);

            g.Children.Add(icon);
            g.Children.Add(text);
            g.Children.Add(control);

            Border card = Styled(new Border(), "Feature");
            card.Child = g;
            return card;
        }

        /// <summary>The two wallpaper-poll rows. The toggle is only meaningful while there
        /// is glass to re-cut, and the interval only while the poll is on.</summary>
        void SyncWatchRows()
        {
            Enable(_watchRow, Core.Config.Blur);
            Enable(_everyRow, Core.Config.Blur && Core.Config.WatchWallpaper);
        }

        /// <summary>Grey a row out along with everything in it.</summary>
        static void Enable(UIElement row, bool on)
        {
            if (row != null) row.IsEnabled = on;
        }

        ComboBox Combo(string[] items, string selected)
        {
            ComboBox c = new ComboBox();
            foreach (string s in items) c.Items.Add(s);
            c.SelectedItem = selected;
            if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
            c.VerticalAlignment = VerticalAlignment.Center;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            return c;
        }

        TextBox Field(string value)
        {
            TextBox t = new TextBox();
            t.Text = value == null ? "" : value;
            t.MaxLength = 24;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        /// <summary>A checkbox, drawn as a switch: the label is the row's, not the
        /// control's, so every control in the panel starts on the same column.</summary>
        CheckBox Switch(bool value)
        {
            CheckBox c = new CheckBox();
            c.IsChecked = value;
            c.VerticalAlignment = VerticalAlignment.Center;
            return c;
        }

        Button Btn(string text, string styleKey)
        {
            Button b = new Button();
            b.Content = text;
            if (!string.IsNullOrEmpty(styleKey))
                b.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            return b;
        }

        UIElement Slide(double min, double max, double value, double tick, Action<double> onChange)
        {
            Grid g = new Grid();
            g.VerticalAlignment = VerticalAlignment.Center;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            Slider s = new Slider();
            s.Minimum = min;
            s.Maximum = max;
            s.Value = Math.Max(min, Math.Min(max, value));
            s.SmallChange = tick;
            s.LargeChange = tick * 5;
            s.IsSnapToTickEnabled = false;
            s.IsMoveToPointEnabled = true;      // clicking the track jumps there
            s.VerticalAlignment = VerticalAlignment.Center;

            TextBlock read = Styled(new TextBlock(), "Value");
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

        static readonly string[] Accents =
        {
            "#FF6FB1FF", "#FF7EE7C4", "#FFFFC46B", "#FFFF8A9B",
            "#FFC79BFF", "#FF9BE36D", "#FFFF9F5A", "#FFE6E9F0"
        };

        UIElement AccentSwatches()
        {
            WrapPanel wrap = new WrapPanel();
            wrap.VerticalAlignment = VerticalAlignment.Center;
            _swatches.Clear();

            foreach (string hex in Accents)
            {
                string h = hex;

                Border dot = new Border();
                dot.Width = 23;
                dot.Height = 23;
                dot.CornerRadius = new CornerRadius(11.5);
                dot.Margin = new Thickness(0, 0, 6, 0);
                dot.Background = new SolidColorBrush(Palette.Parse(h, Colors.SkyBlue));
                dot.Cursor = Cursors.Hand;

                // The tick, not a ring: on eight pastel circles a ring reads as a ninth
                // colour, and the mark has to survive whichever of them is underneath it.
                TextBlock tick = new TextBlock();
                tick.Text = MenuChrome.GlyphDone;
                tick.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                tick.FontSize = 12;
                tick.Foreground = new SolidColorBrush(Color.FromRgb(20, 22, 28));
                tick.HorizontalAlignment = HorizontalAlignment.Center;
                tick.VerticalAlignment = VerticalAlignment.Center;
                dot.Child = tick;

                dot.MouseLeftButtonUp += delegate
                {
                    if (_loading) return;
                    Core.Config.Accent = h;
                    Push();
                    Restyle();
                };

                _swatches.Add(new KeyValuePair<string, TextBlock>(h, tick));
                wrap.Children.Add(dot);
            }

            MarkAccent();
            return wrap;
        }

        void MarkAccent()
        {
            foreach (KeyValuePair<string, TextBlock> s in _swatches)
            {
                s.Value.Visibility =
                    string.Equals(s.Key, Core.Config.Accent, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ====================================================================== styles

        static string Hex(Color c, double alpha)
        {
            double a = alpha < 0 ? 0 : (alpha > 1 ? 1 : alpha);
            return "#" + ((byte)Math.Round(a * 255)).ToString("X2")
                 + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static string Hex(Brush b, double alpha)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return Hex(s != null ? s.Color : Colors.Transparent, alpha);
        }

        static string Opaque(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return Hex(s != null ? s.Color : Colors.Black, 1);
        }

        static string Num(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>The accent, taken down towards black. The accents are all pastels, which
        /// is what makes them work as fills and as a wash behind a row - and what makes them
        /// nearly invisible as *text* on a light panel. Headings and glyphs use this instead,
        /// so the light theme reads as firmly as the dark one does.</summary>
        static Color Ink(Color c, double towardsBlack)
        {
            double k = 1 - towardsBlack;
            return Color.FromRgb((byte)Math.Round(c.R * k),
                                 (byte)Math.Round(c.G * k),
                                 (byte)Math.Round(c.B * k));
        }

        /// <summary>Every colour and metric the panel uses, in one dictionary. Same idea as
        /// MenuChrome's: the templates are markup because a template is markup, and the
        /// palette is substituted into them so it stays the single source.</summary>
        static ResourceDictionary BuildStyles(Palette p)
        {
            double fs = BodySize();
            Color accent = p.AccentColor;
            Color hair = p.IsDark ? Colors.White : Colors.Black;

            string xaml = Xaml
                .Replace("@fs@", Num(fs))
                .Replace("@fshead@", Num(fs + 3.5))
                .Replace("@fsnote@", Num(Math.Max(10.5, fs - 1.5)))
                .Replace("@text@", Hex(p.Text, 1))
                .Replace("@muted@", Hex(p.Muted, 1))
                .Replace("@divider@", Hex(hair, p.IsDark ? 0.11 : 0.10))
                .Replace("@border@", Hex(hair, p.IsDark ? 0.10 : 0.09))
                .Replace("@fill@", Hex(p.Panel, MenuChrome.FlatAlpha))
                .Replace("@panel@", Opaque(p.Panel))
                .Replace("@field@", Hex(hair, p.IsDark ? 0.07 : 0.04))
                .Replace("@fieldbd@", Hex(hair, p.IsDark ? 0.14 : 0.13))
                .Replace("@track@", Hex(hair, p.IsDark ? 0.20 : 0.18))
                .Replace("@accent@", Hex(accent, 1))
                .Replace("@accentink@", Hex(p.IsDark ? accent : Ink(accent, 0.42), 1))
                .Replace("@hover@", Hex(accent, p.IsDark ? 0.19 : 0.16))
                .Replace("@pressed@", Hex(accent, p.IsDark ? 0.30 : 0.26))
                .Replace("@onaccent@", "#FF14161C");

            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        // Single quotes throughout - it saves doubling every quote in the C# literal.
        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <SolidColorBrush x:Key='TextBrush' Color='@text@'/>
  <SolidColorBrush x:Key='MutedBrush' Color='@muted@'/>
  <SolidColorBrush x:Key='AccentBrush' Color='@accent@'/>
  <SolidColorBrush x:Key='AccentInkBrush' Color='@accentink@'/>
  <SolidColorBrush x:Key='DividerBrush' Color='@divider@'/>
  <SolidColorBrush x:Key='CardBorderBrush' Color='@border@'/>
  <SolidColorBrush x:Key='HoverBrush' Color='@hover@'/>
  <SolidColorBrush x:Key='FieldBrush' Color='@field@'/>
  <SolidColorBrush x:Key='FieldBorderBrush' Color='@fieldbd@'/>

  <!-- ============================================================ text -->

  <Style x:Key='Head' TargetType='{x:Type TextBlock}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fshead@'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='VerticalAlignment' Value='Center'/>
  </Style>

  <Style x:Key='Section' TargetType='{x:Type TextBlock}'>
    <Setter Property='Foreground' Value='{StaticResource AccentInkBrush}'/>
    <Setter Property='FontSize' Value='10.5'/>
    <Setter Property='FontWeight' Value='Bold'/>
    <Setter Property='VerticalAlignment' Value='Center'/>
  </Style>

  <Style x:Key='Label' TargetType='{x:Type TextBlock}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='TextWrapping' Value='Wrap'/>
  </Style>

  <Style x:Key='Note' TargetType='{x:Type TextBlock}'>
    <Setter Property='Foreground' Value='{StaticResource MutedBrush}'/>
    <Setter Property='FontSize' Value='@fsnote@'/>
    <Setter Property='TextWrapping' Value='Wrap'/>
  </Style>

  <Style x:Key='Value' TargetType='{x:Type TextBlock}'>
    <Setter Property='Foreground' Value='{StaticResource MutedBrush}'/>
    <Setter Property='FontSize' Value='@fsnote@'/>
    <Setter Property='TextAlignment' Value='Right'/>
    <Setter Property='VerticalAlignment' Value='Center'/>
  </Style>

  <Style x:Key='Glyph' TargetType='{x:Type TextBlock}'>
    <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
    <Setter Property='Foreground' Value='{StaticResource AccentInkBrush}'/>
    <Setter Property='VerticalAlignment' Value='Center'/>
  </Style>

  <!-- ============================================================ rows -->

  <Style x:Key='Row' TargetType='{x:Type Border}'>
    <Setter Property='CornerRadius' Value='6'/>
    <Setter Property='Padding' Value='10,6,10,6'/>
    <Setter Property='Margin' Value='0,1,0,1'/>
    <Setter Property='Background' Value='#00FFFFFF'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter Property='Background' Value='@hover@'/>
      </Trigger>
      <Trigger Property='IsEnabled' Value='False'>
        <Setter Property='Opacity' Value='0.4'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key='Feature' TargetType='{x:Type Border}'>
    <Setter Property='CornerRadius' Value='8'/>
    <Setter Property='Padding' Value='12,11,12,11'/>
    <Setter Property='Margin' Value='10,6,10,2'/>
    <Setter Property='Background' Value='@field@'/>
    <Setter Property='BorderBrush' Value='@fieldbd@'/>
    <Setter Property='BorderThickness' Value='1'/>
  </Style>

  <!-- ========================================================== switches -->

  <Style TargetType='{x:Type CheckBox}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='HorizontalAlignment' Value='Left'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type CheckBox}'>
          <Grid Width='42' Height='24' Background='#00FFFFFF'>
            <Border x:Name='Pill' CornerRadius='12' Background='@track@'
                    BorderBrush='@fieldbd@' BorderThickness='1'/>
            <Ellipse x:Name='Knob' Width='12' Height='12' Fill='@text@'
                     HorizontalAlignment='Left' Margin='6,0,0,0'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Pill' Property='Background' Value='@accent@'/>
              <Setter TargetName='Pill' Property='BorderBrush' Value='@accent@'/>
              <Setter TargetName='Knob' Property='Fill' Value='@onaccent@'/>
              <Setter TargetName='Knob' Property='HorizontalAlignment' Value='Right'/>
              <Setter TargetName='Knob' Property='Margin' Value='0,0,6,0'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Knob' Property='Width' Value='14'/>
              <Setter TargetName='Knob' Property='Height' Value='14'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- =========================================================== sliders -->

  <Style x:Key='SliderThumb' TargetType='{x:Type Thumb}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Width' Value='18'/>
    <Setter Property='Height' Value='18'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Thumb}'>
          <Grid Background='#00FFFFFF'>
            <Ellipse Fill='@panel@' Stroke='@fieldbd@' StrokeThickness='1'/>
            <Ellipse x:Name='Dot' Width='9' Height='9' Fill='@accent@'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Dot' Property='Width' Value='11'/>
              <Setter TargetName='Dot' Property='Height' Value='11'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='Dot' Property='Width' Value='7'/>
              <Setter TargetName='Dot' Property='Height' Value='7'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='TrackDone' TargetType='{x:Type RepeatButton}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RepeatButton}'>
          <Grid Background='#00FFFFFF'>
            <Border Height='4' CornerRadius='2' VerticalAlignment='Center' Background='@accent@'/>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='TrackTodo' TargetType='{x:Type RepeatButton}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RepeatButton}'>
          <Grid Background='#00FFFFFF'>
            <Border Height='4' CornerRadius='2' VerticalAlignment='Center' Background='@track@'/>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type Slider}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='MinHeight' Value='26'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Slider}'>
          <Grid Background='#00FFFFFF' MinHeight='26'>
            <Track x:Name='PART_Track'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Style='{StaticResource TrackDone}' Command='Slider.DecreaseLarge'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style='{StaticResource SliderThumb}'/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style='{StaticResource TrackTodo}' Command='Slider.IncreaseLarge'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ============================================================ fields -->

  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='CaretBrush' Value='@accent@'/>
    <Setter Property='SelectionBrush' Value='@accent@'/>
    <Setter Property='Padding' Value='10,6,10,7'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type TextBox}'>
          <Border x:Name='Bd' CornerRadius='6' Background='@field@'
                  BorderBrush='@fieldbd@' BorderThickness='1' SnapsToDevicePixels='True'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'
                          VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='@hover@'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocusWithin' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='@accent@'/>
              <Setter TargetName='Bd' Property='Background' Value='@field@'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ========================================================== dropdowns -->

  <Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='Padding' Value='11,7,11,8'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ComboBoxItem}'>
          <Border x:Name='Fill' CornerRadius='4' Background='#00FFFFFF'
                  Padding='{TemplateBinding Padding}'>
            <ContentPresenter VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Fill' Property='Background' Value='@hover@'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='Fill' Property='Background' Value='@pressed@'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Focusable is left alone here: a ComboBox closes its own dropdown when keyboard
       focus cannot land on it, so a non-focusable one opens and shuts again on the click
       that opened it. The face inside it is the part that never takes focus. -->
  <Style TargetType='{x:Type ComboBox}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ComboBox}'>
          <Grid>
            <ToggleButton x:Name='Face' Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay,
                                     RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='{x:Type ToggleButton}'>
                  <Border x:Name='Bd' CornerRadius='6' Background='@field@'
                          BorderBrush='@fieldbd@' BorderThickness='1' SnapsToDevicePixels='True'>
                    <TextBlock x:Name='Chevron' Text='&#xE70D;' Margin='0,0,11,0'
                               HorizontalAlignment='Right' VerticalAlignment='Center'
                               FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets'
                               FontSize='10' Foreground='@muted@'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='Bd' Property='Background' Value='@hover@'/>
                      <Setter TargetName='Chevron' Property='Foreground' Value='@text@'/>
                    </Trigger>
                    <Trigger Property='IsChecked' Value='True'>
                      <Setter TargetName='Bd' Property='Background' Value='@hover@'/>
                      <Setter TargetName='Bd' Property='BorderBrush' Value='@accent@'/>
                      <Setter TargetName='Chevron' Property='Foreground' Value='@accent@'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>

            <ContentPresenter Content='{TemplateBinding SelectionBoxItem}'
                              ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                              IsHitTestVisible='False' VerticalAlignment='Center'
                              Margin='11,6,32,7'/>

            <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True'
                   Focusable='False' HorizontalOffset='-8' VerticalOffset='-2'
                   IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border Margin='8,4,8,12' CornerRadius='8' Background='@fill@'
                      BorderBrush='@border@' BorderThickness='1' SnapsToDevicePixels='True'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
                <Border.Effect>
                  <DropShadowEffect Color='#FF000000' BlurRadius='14' ShadowDepth='4'
                                    Direction='270' Opacity='0.36'/>
                </Border.Effect>
                <ScrollViewer MaxHeight='260' VerticalScrollBarVisibility='Auto'>
                  <StackPanel IsItemsHost='True' Margin='4'
                              KeyboardNavigation.DirectionalNavigation='Contained'/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- =========================================================== buttons -->

  <Style TargetType='{x:Type Button}'>
    <Setter Property='Foreground' Value='{StaticResource TextBrush}'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='Padding' Value='14,6,14,7'/>
    <Setter Property='MinWidth' Value='88'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='Bd' CornerRadius='6' Background='@field@'
                  BorderBrush='@fieldbd@' BorderThickness='1'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='@hover@'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='@pressed@'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.4'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='AccentButton' TargetType='{x:Type Button}'>
    <Setter Property='Foreground' Value='@onaccent@'/>
    <Setter Property='FontSize' Value='@fs@'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='14,6,14,7'/>
    <Setter Property='MinWidth' Value='88'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='Bd' CornerRadius='6' Background='@accent@'
                  Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Opacity' Value='0.88'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Opacity' Value='0.74'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='IconButton' TargetType='{x:Type Button}'>
    <Setter Property='Foreground' Value='{StaticResource MutedBrush}'/>
    <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Width' Value='32'/>
    <Setter Property='Height' Value='28'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='VerticalAlignment' Value='Top'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='Bd' CornerRadius='5' Background='#00FFFFFF'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='@hover@'/>
              <Setter Property='Foreground' Value='@text@'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='@pressed@'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ======================================================== scrollbars -->

  <Style x:Key='ScrollThumb' TargetType='{x:Type Thumb}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Thumb}'>
          <Border x:Name='Bar' Width='5' CornerRadius='3' Background='@track@'
                  HorizontalAlignment='Center'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bar' Property='Width' Value='7'/>
              <Setter TargetName='Bar' Property='Background' Value='@muted@'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='Bar' Property='Width' Value='7'/>
              <Setter TargetName='Bar' Property='Background' Value='@accent@'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ScrollPage' TargetType='{x:Type RepeatButton}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RepeatButton}'>
          <Border Background='#00FFFFFF'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type ScrollBar}'>
    <Setter Property='Width' Value='11'/>
    <Setter Property='MinWidth' Value='11'/>
    <Setter Property='Background' Value='#00FFFFFF'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ScrollBar}'>
          <Grid Background='#00FFFFFF'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageUpCommand'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style='{StaticResource ScrollThumb}'/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style='{StaticResource ScrollPage}' Command='ScrollBar.PageDownCommand'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>";
    }
}
