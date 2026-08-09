using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>The bar itself: day columns across, one row per week, living on the
    /// desktop underneath every real window.</summary>
    internal class WallWindow : Window
    {
        static readonly string[] DayNames = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };

        class Cell
        {
            public Border Root;
            public TextBlock Date;
            public ScrollViewer Scroller;
            public StackPanel List;
            public Border AddRow;
        }

        Cell[,] _cells;                 // [week, day]
        TextBlock[,] _headers;          // [week, day] - each week block owns its own day names

        Border _shell;                  // the panel: shorter than the window while a week is folding
        Border _tint;
        Grid _bodyGrid;
        FrameworkElement _topStrip;
        Grid[] _weekBlocks;             // one per week, each given an identical fixed height
        Border[] _weekFrames;           // the rule-and-padding wrapper around each block
        TextBlock _weekLabel;
        TextBlock _toggleLabel;         // "Show/Hide next week" - flipped without a rebuild
        Border _toggleButton;

        ImageBrush _glassBrush;         // the frosted backdrop, pinned to the envelope
        double _glassW, _glassEnvH;     // the rect it was cut for, in DIP

        int _treeWeeks;                 // week blocks actually built - not always WeekRows, mid-fold
        double _perWeek;                // the height every week block currently has
        double _shellH, _shellH0, _shellH1;

        IntPtr _hwnd = IntPtr.Zero;
        IntPtr _parent = IntPtr.Zero;
        HostMode _mode = HostMode.Floating;
        bool _childAttached;
        string _backdropKey = "";

        int _placedY, _placedH;         // the window rect Relayout last asked for, in pixels

        DispatcherTimer _watchdog;
        DateTime _lastSeenDate = DateTime.MinValue;
        DateTime _viewMonday;
        QuickEdit _editor;
        bool _firstLayout = true;

        readonly Dictionary<string, Week> _blanks = new Dictionary<string, Week>(StringComparer.Ordinal);

        public WallWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Title = "TodoWall";

            // Park off-screen until Relayout() places it, so nothing flashes at 0,0.
            Left = -20000;
            Top = -20000;
            Width = 800;
            Height = 240;

            _viewMonday = Board.MondayOf(DateTime.Today);
            _lastSeenDate = DateTime.Today;

            BuildChrome();
            RefreshAll();
            Reveal = WeekRows == 2 ? 1 : 0;
        }

        /// <summary>True while the user is mid-interaction, so background housekeeping
        /// (which can cause page faults) can politely wait.</summary>
        public bool IsBusy { get { return _editor != null || IsMouseOver; } }

        public IntPtr Hwnd { get { return _hwnd; } }

        double FS { get { return Core.Config.FontSize; } }
        int WeekRows { get { return Core.Config.WeekRows < 2 ? 1 : 2; } }
        int DayCount { get { return Core.Config.ShowWeekend ? 7 : 5; } }

        // The gap introduced between two stacked weeks: breathing room, the 1px rule, then
        // breathing room again. Relayout adds this back so the weeks themselves stay equal.
        double SeparatorMargin { get { return FS * 0.45; } }
        double SeparatorPad { get { return FS * 0.5; } }
        double SeparatorHeight { get { return SeparatorMargin + SeparatorPad + 1; } }

        /// <summary>Which edge of the window the panel is pinned to - the edge the user
        /// asked the bar to sit against. It is what makes a growing bar grow away from that
        /// edge instead of sliding along it.</summary>
        VerticalAlignment Anchor
        {
            get
            {
                if (string.Equals(Core.Config.VAlign, "Top", StringComparison.OrdinalIgnoreCase))
                    return VerticalAlignment.Top;
                if (string.Equals(Core.Config.VAlign, "Middle", StringComparison.OrdinalIgnoreCase))
                    return VerticalAlignment.Center;
                return VerticalAlignment.Bottom;
            }
        }

        /// <summary>The same thing as a number: how much of the slack between the panel and
        /// the envelope sits above the panel.</summary>
        double AnchorFactor
        {
            get
            {
                VerticalAlignment a = Anchor;
                if (a == VerticalAlignment.Top) return 0;
                return a == VerticalAlignment.Center ? 0.5 : 1;
            }
        }

        /// <summary>Everything above and around the week blocks: the shell border, the
        /// panel padding, the ◀/▶ strip, and the gap under it. Independent of how many
        /// weeks are shown, which is what makes a week's own height a fixed quantity.</summary>
        double ChromeHeight()
        {
            double strip = 0;
            if (_topStrip != null)
            {
                strip = _topStrip.DesiredSize.Height;
                if (strip <= 0)
                {
                    // First layout - nothing has been measured yet.
                    _topStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    strip = _topStrip.DesiredSize.Height;
                }
            }
            return 2 /* shell border */ + FS * 0.8 * 2 /* panel padding */ + strip + FS * 0.45 /* gap */;
        }

        // ===================================================================== chrome

        void BuildChrome()
        {
            Palette p = Core.Skin;

            _shell = new Border();
            // The backdrop is a pre-blurred thumbnail stretched over the whole bar, and
            // software rendering resamples it for every dirty rectangle. Nothing else
            // under here is a bitmap, and bilinear on an already-blurred image is
            // indistinguishable from the expensive filter.
            RenderOptions.SetBitmapScalingMode(_shell, BitmapScalingMode.LowQuality);
            _shell.CornerRadius = new CornerRadius(Core.Config.CornerRadius);
            _shell.Background = p.Panel;
            _shell.BorderBrush = p.PanelBorder;
            _shell.BorderThickness = new Thickness(1);
            _shell.ContextMenu = BuildPanelMenu();

            _tint = new Border();
            _tint.CornerRadius = new CornerRadius(Core.Config.CornerRadius);
            _tint.Background = Brushes.Transparent;

            Grid root = new Grid();
            root.Margin = new Thickness(FS * 0.8);
            // Mid-morph the panel is deliberately shorter than what it contains: the week
            // being folded away has to be cut off at the padding rather than spill out over
            // the glass and past the bottom of the bar.
            root.ClipToBounds = true;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _topStrip = BuildTopStrip();
            root.Children.Add(_topStrip);

            _bodyGrid = new Grid();
            _bodyGrid.Margin = new Thickness(0, FS * 0.45, 0, 0);
            Grid.SetRow(_bodyGrid, 1);
            root.Children.Add(_bodyGrid);

            BuildCells();

            Grid outer = new Grid();
            outer.Children.Add(_tint);
            outer.Children.Add(root);
            _shell.Child = outer;

            // The panel is not the window. While a week is folding in or out the window is
            // cut for the taller of the two states and the panel travels inside it, pinned
            // to the edge the bar sits against - see ApplyReveal.
            _shell.VerticalAlignment = Anchor;
            Grid stage = new Grid();
            stage.Children.Add(_shell);
            Content = stage;

            _glassBrush = null;
            _backdropKey = ""; // force the backdrop to be rebuilt for the new geometry
        }

        FrameworkElement BuildTopStrip()
        {
            Palette p = Core.Skin;
            Grid strip = new Grid();
            Grid.SetRow(strip, 0);

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.HorizontalAlignment = HorizontalAlignment.Left;

            left.Children.Add(TinyButton("◀", "Previous week", delegate { ShiftWeek(-1); }));

            _weekLabel = new TextBlock();
            _weekLabel.FontSize = FS * 0.88;
            _weekLabel.FontWeight = FontWeights.SemiBold;
            _weekLabel.Foreground = p.Muted;
            _weekLabel.VerticalAlignment = VerticalAlignment.Center;
            _weekLabel.Margin = new Thickness(FS * 0.3, 0, FS * 0.3, 0);
            _weekLabel.MinWidth = FS * 11;
            _weekLabel.TextAlignment = TextAlignment.Center;
            left.Children.Add(_weekLabel);

            left.Children.Add(TinyButton("▶", "Next week", delegate { ShiftWeek(1); }));
            left.Children.Add(TinyButton("Today", "Jump back to this week",
                delegate { GoToWeekOf(DateTime.Today); }));

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(TinyButton("Clear done", "Remove completed tasks from both weeks", ClearCompleted));

            // Kept, so the caption can flip the instant it is pressed - the transition it
            // starts outlives the click, and rebuilding the strip mid-morph would take the
            // panel apart underneath it.
            _toggleButton = TinyButton("", "", ToggleNextWeek);
            _toggleLabel = _toggleButton.Child as TextBlock;
            SetToggleLabel(WeekRows == 2);
            right.Children.Add(_toggleButton);
            right.Children.Add(TinyButton("⚙", "Settings", delegate { SettingsWindow.ShowSingleton(); }));

            strip.Children.Add(left);
            strip.Children.Add(right);
            return strip;
        }

        void SetToggleLabel(bool twoWeeks)
        {
            if (_toggleLabel != null)
                _toggleLabel.Text = twoWeeks ? "Hide next week" : "Show next week";
            if (_toggleButton != null)
                _toggleButton.ToolTip = twoWeeks
                    ? "Show this week only" : "Stack the following week underneath";
        }

        Border TinyButton(string glyph, string tip, Action onClick)
        {
            Palette p = Core.Skin;
            Color mutedColor = ColorOf(p.Muted);
            Color textColor = ColorOf(p.Text);
            Color hoverColor = ColorOf(p.HoverRow);

            SolidColorBrush fg = new SolidColorBrush(mutedColor);
            SolidColorBrush bg = new SolidColorBrush(Colors.Transparent);

            TextBlock tb = new TextBlock();
            tb.Text = glyph;
            tb.FontSize = FS * 0.85;
            tb.Foreground = fg;
            tb.VerticalAlignment = VerticalAlignment.Center;

            Border b = new Border();
            b.Child = tb;
            b.Padding = new Thickness(FS * 0.42, FS * 0.16, FS * 0.42, FS * 0.16);
            b.Margin = new Thickness(FS * 0.1, 0, FS * 0.1, 0);
            b.CornerRadius = new CornerRadius(5);
            b.Background = bg;
            b.Cursor = Cursors.Hand;
            b.ToolTip = tip;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = new ScaleTransform(1, 1);

            b.MouseEnter += delegate
            {
                Anim.Tint(bg, hoverColor, Anim.HoverIn);
                Anim.Tint(fg, textColor, Anim.HoverIn);
            };
            b.MouseLeave += delegate
            {
                Anim.Tint(bg, Colors.Transparent, Anim.HoverOut);
                Anim.Tint(fg, mutedColor, Anim.HoverOut);
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Anim.Pop(b, 0.9, 180);
                onClick();
            };
            return b;
        }

        static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Transparent;
        }

        /// <summary>Build every week block from scratch. Only for a fresh chrome - folding
        /// goes through <see cref="SetWeekCount"/>, which leaves the week you are already
        /// looking at alone.</summary>
        void BuildCells()
        {
            _bodyGrid.Children.Clear();
            _bodyGrid.ColumnDefinitions.Clear();
            _bodyGrid.RowDefinitions.Clear();

            _cells = new Cell[0, 7];
            _headers = new TextBlock[0, 7];
            _weekBlocks = new Grid[0];
            _weekFrames = new Border[0];
            _treeWeeks = 0;

            SetWeekCount(WeekRows);
        }

        /// <summary>
        /// Grow or shrink the stack of week blocks, building only what is new and leaving
        /// what stays exactly as it was - scroll positions, hover states and all.
        ///
        /// A week is seven columns of task rows and is far and away the most expensive
        /// thing this window builds. Rebuilding the week already on screen at the very
        /// moment a fold starts or ends is precisely where the eye catches a blink, so the
        /// fold never does: opening adds a block, closing takes one away.
        /// </summary>
        void SetWeekCount(int weeks)
        {
            if (weeks == _treeWeeks) return;

            for (int wk = _treeWeeks - 1; wk >= weeks; wk--)
            {
                _bodyGrid.Children.Remove(_weekFrames[wk]);
                _bodyGrid.RowDefinitions.RemoveAt(wk);
            }

            // Rows are Auto, not Star: the blocks are handed an explicit, identical height
            // in MeasureBlocks and the panel is sized to fit them. Star would instead carve
            // one bar's worth of space into N, which shrinks every week as you add one.
            for (int wk = _treeWeeks; wk < weeks; wk++)
                _bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int keep = Math.Min(weeks, _treeWeeks);
            Cell[,] cells = new Cell[weeks, 7];
            TextBlock[,] headers = new TextBlock[weeks, 7];
            Grid[] blocks = new Grid[weeks];
            Border[] frames = new Border[weeks];
            for (int wk = 0; wk < keep; wk++)
            {
                blocks[wk] = _weekBlocks[wk];
                frames[wk] = _weekFrames[wk];
                for (int d = 0; d < 7; d++)
                {
                    cells[wk, d] = _cells[wk, d];
                    headers[wk, d] = _headers[wk, d];
                }
            }
            _cells = cells;
            _headers = headers;
            _weekBlocks = blocks;
            _weekFrames = frames;

            for (int wk = keep; wk < weeks; wk++) BuildWeek(wk);
            _treeWeeks = weeks;
        }

        /// <summary>One week block: its own day names, its own seven columns.
        ///
        /// Each week is a complete week in its own right, stacked - not a second row
        /// threaded through the first week's columns under one shared header. Every block
        /// declares its own 7 star columns across the full bar width, so a week looks and
        /// measures exactly the same whether one or two are shown.</summary>
        void BuildWeek(int wk)
        {
            Palette p = Core.Skin;
            int days = DayCount;

            Grid block = new Grid();
            for (int d = 0; d < days; d++)
                block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // day names
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // days

            // A rule between the blocks, so the eye reads two weeks and not one grid.
            Border frame = new Border();
            frame.Child = block;
            frame.BorderBrush = p.Divider;
            frame.BorderThickness = new Thickness(0, wk == 0 ? 0 : 1, 0, 0);
            frame.Padding = new Thickness(0, wk == 0 ? 0 : SeparatorPad, 0, 0);
            frame.Margin = new Thickness(0, wk == 0 ? 0 : SeparatorMargin, 0, 0);
            Grid.SetRow(frame, wk);
            _bodyGrid.Children.Add(frame);
            _weekBlocks[wk] = block;
            _weekFrames[wk] = frame;

            for (int d = 0; d < days; d++)
            {
                TextBlock h = new TextBlock();
                h.Text = DayNames[d];
                h.FontSize = FS * 0.82;
                h.FontWeight = FontWeights.Bold;
                h.Foreground = p.DayName;
                h.Margin = new Thickness(FS * 0.55, 0, 0, FS * 0.3);
                Grid.SetColumn(h, d);
                Grid.SetRow(h, 0);
                block.Children.Add(h);
                _headers[wk, d] = h;
            }

            for (int d = 0; d < days; d++)
            {
                Cell c = new Cell();

                Grid inner = new Grid();
                inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                c.Date = new TextBlock();
                c.Date.FontSize = FS * 0.78;
                c.Date.FontWeight = FontWeights.SemiBold;
                c.Date.Foreground = p.DayDate;
                c.Date.Margin = new Thickness(FS * 0.3, 0, 0, FS * 0.2);
                Grid.SetRow(c.Date, 0);
                inner.Children.Add(c.Date);

                c.List = new StackPanel();
                c.Scroller = new ScrollViewer();
                c.Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                c.Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                c.Scroller.Content = c.List;
                c.Scroller.Padding = new Thickness(0, 0, FS * 0.15, 0);
                Grid.SetRow(c.Scroller, 1);
                inner.Children.Add(c.Scroller);

                ScrollViewer sv = c.Scroller;
                sv.ScrollChanged += delegate { UpdateScrollFade(sv); };
                sv.SizeChanged += delegate { UpdateScrollFade(sv); };

                // The add row lives at the end of the task list rather than pinned to
                // the bottom of the cell, so an empty day doesn't show a dead gap.
                int week = wk, day = d;
                c.AddRow = BuildAddRow(week, day);

                c.Root = new Border();
                c.Root.Child = inner;
                c.Root.CornerRadius = new CornerRadius(9);
                c.Root.Padding = new Thickness(FS * 0.4, FS * 0.3, FS * 0.35, FS * 0.25);
                c.Root.Margin = new Thickness(d == 0 ? 0 : FS * 0.18, 0, FS * 0.18, FS * 0.2);
                c.Root.Background = Brushes.Transparent;
                c.Root.BorderBrush = p.Divider;
                c.Root.BorderThickness = new Thickness(d == 0 ? 0 : 1, 0, 0, 0);

                Grid.SetColumn(c.Root, d);
                Grid.SetRow(c.Root, 1);
                block.Children.Add(c.Root);
                _cells[wk, d] = c;
            }
        }

        /// <summary>Soften the cut where a column's tasks overflow its cell, and only then -
        /// a permanent fade would dim the last task in columns that fit fine.
        ///
        /// An OpacityMask makes WPF render the whole scroller to an offscreen surface and
        /// mask it, so touching this property re-rasterises the entire column. It fires on
        /// every scroll notification, but only ever has three outcomes (at the top, in the
        /// middle, at the bottom) - so the assignment is skipped unless one of those
        /// actually flipped.</summary>
        static void UpdateScrollFade(ScrollViewer sv)
        {
            double h = sv.ActualHeight;
            if (h <= 0 || sv.ScrollableHeight <= 0.5)
            {
                if (sv.OpacityMask != null) { sv.OpacityMask = null; sv.Tag = null; }
                return;
            }

            double fade = Math.Min(20, h * 0.2) / h;
            bool more = sv.VerticalOffset < sv.ScrollableHeight - 0.5;
            bool above = sv.VerticalOffset > 0.5;

            string sig = fade.ToString("0.0000", CultureInfo.InvariantCulture)
                + (above ? "|a" : "|") + (more ? "|m" : "|");
            if (sv.OpacityMask != null && (sv.Tag as string) == sig) return;
            sv.Tag = sig;

            LinearGradientBrush mask = new LinearGradientBrush();
            mask.StartPoint = new Point(0, 0);
            mask.EndPoint = new Point(0, 1);
            mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, above ? fade : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, more ? 1 - fade : 1));
            mask.GradientStops.Add(new GradientStop(more ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            sv.OpacityMask = mask;
        }

        Border BuildAddRow(int week, int day)
        {
            Palette p = Core.Skin;
            TextBlock tb = new TextBlock();
            tb.Text = "+  add task";
            tb.FontSize = FS * 0.85;
            tb.Foreground = p.Muted;

            Border b = new Border();
            b.Child = tb;
            b.Padding = new Thickness(FS * 0.32, FS * 0.26, FS * 0.32, FS * 0.26);
            b.Margin = new Thickness(0, FS * 0.2, 0, 0);
            b.CornerRadius = new CornerRadius(6);
            b.Cursor = Cursors.Hand;
            b.Opacity = 0.5;

            SolidColorBrush bg = new SolidColorBrush(Colors.Transparent);
            b.Background = bg;

            b.MouseEnter += delegate
            {
                Anim.Fade(b, 1, Anim.HoverIn);
                Anim.Tint(bg, ColorOf(p.HoverRow), Anim.HoverIn);
            };
            b.MouseLeave += delegate
            {
                Anim.Fade(b, 0.5, Anim.HoverOut);
                Anim.Tint(bg, Colors.Transparent, Anim.HoverOut);
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                StartAdd(week, day);
            };
            return b;
        }

        ContextMenu BuildPanelMenu()
        {
            ContextMenu m = new ContextMenu();
            m.Items.Add(MenuItemFor("Settings…", MenuChrome.GlyphSettings,
                delegate { SettingsWindow.ShowSingleton(); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItemFor("Clear completed", MenuChrome.GlyphClear,
                delegate { ClearCompleted(); }));
            m.Items.Add(MenuItemFor("Re-attach to desktop", MenuChrome.GlyphAttach,
                delegate { Attach(true); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItemFor("Exit TodoWall", MenuChrome.GlyphExit, delegate
            {
                Core.SaveBoardNow();
                Application.Current.Shutdown();
            }));
            return m;
        }

        static MenuItem MenuItemFor(string header, string glyph, Action act)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            mi.Icon = glyph;
            mi.Click += delegate { act(); };
            return mi;
        }

        // ===================================================================== data

        public void ApplySettingsChanged()
        {
            Core.RebuildSkin();
            MenuChrome.Refresh();
            Anim.Enabled = Core.Config.Animations;
            BuildChrome();
            RefreshAll();

            // Land on the end state rather than replaying the fold: this runs on every drag
            // of a settings slider.
            BeginAnimation(RevealProperty, null);
            Reveal = WeekRows == 2 ? 1 : 0;
            Relayout();
        }

        // ===================================================================== the fold

        /// <summary>How far the second week is unfolded: 0 is this week alone, 1 is both.
        /// A dependency property so WPF's own animation clock can drive it - everything the
        /// transition consists of is a function of this one number, exactly as the clock's
        /// calendar is.</summary>
        public static readonly DependencyProperty RevealProperty =
            DependencyProperty.Register("Reveal", typeof(double), typeof(WallWindow),
                new PropertyMetadata(0.0, OnRevealChanged));

        public double Reveal
        {
            get { return (double)GetValue(RevealProperty); }
            set { SetValue(RevealProperty, value); }
        }

        static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WallWindow)d).ApplyReveal();
        }

        /// <summary>Fold the following week in or out.
        ///
        /// Lives on the strip rather than in Settings because it is a view toggle people
        /// flip several times a day, but it still persists so the bar comes back the way it
        /// was left.
        ///
        /// The week itself is built here and dropped again in <see cref="EndMorph"/>: it is
        /// seven columns of rows, and while it is folded away there is no reason for any of
        /// it to exist, let alone be laid out and rasterised on every repaint of the bar.
        /// </summary>
        void ToggleNextWeek()
        {
            bool showing = WeekRows == 2;
            Core.Config.WeekRows = showing ? 1 : 2;
            Core.Config.Save();
            SetToggleLabel(!showing);
            UpdateWeekLabel();

            if (!Anim.Enabled)
            {
                SetWeekCount(WeekRows);
                if (!showing) RefreshWeek(1);
                BeginAnimation(RevealProperty, null);
                Reveal = showing ? 0 : 1;
                Remeasure();
                return;
            }

            // Opening: the week has to be there before it can be revealed. Pressing the
            // button again mid-fold just reverses the travel - the block is already up.
            if (!showing && _treeWeeks < 2)
            {
                SetWeekCount(2);
                RefreshWeek(1);
            }

            // A week-switch fade may still be holding this frame's opacity, and a held
            // animation outranks the local value ApplyReveal sets - see Anim.Rewind.
            if (_weekFrames != null && _weekFrames.Length > 1 && _weekFrames[1] != null)
                _weekFrames[1].BeginAnimation(OpacityProperty, null);

            Remeasure();                 // the new block needs a height; the window is untouched
            AnimateReveal(!showing);
        }

        void AnimateReveal(bool open)
        {
            DoubleAnimation a = new DoubleAnimation();
            a.To = open ? 1 : 0;
            // Out is the gesture people watch; folding back just needs to get out of the way.
            a.Duration = new Duration(TimeSpan.FromMilliseconds(open ? 320 : 240));
            CubicEase e = new CubicEase();
            e.EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn;
            a.EasingFunction = e;
            a.Completed += delegate { EndMorph(); };
            BeginAnimation(RevealProperty, a);
        }

        /// <summary>
        /// The whole transition.
        ///
        /// The panel is a slab of content of a fixed height - two weeks' worth, whatever is
        /// showing - and this sets how much of it the panel is allowed to be. Nothing
        /// inside reflows: the week blocks keep the height they were handed, so the second
        /// week is simply cut off by the panel's clip until there is room for it, and a
        /// frame costs one Border and the strip of glass it just uncovered.
        ///
        /// The window itself does not move for any of this - not per frame, and not at the
        /// ends either. It is cut for the two-week envelope at all times and the panel is
        /// pinned inside it, so a fold is a panel height and nothing else. Resizing a
        /// layered window hands it a new origin before anything has been painted into the
        /// new surface, and the frame or two of old-picture-at-new-position that follows is
        /// exactly the blink a transition must not open or close with.
        /// </summary>
        void ApplyReveal()
        {
            if (_shell == null || _shellH1 <= 0) return;

            double t = Reveal;
            _shellH = _shellH0 + (_shellH1 - _shellH0) * t;
            _shell.Height = _shellH;
            PinGlass(_shellH);

            // The greeting pill rides the panel's edge, so it travels with a fold. Its own
            // window is cut for the whole band, so this is a margin - not a window move.
            if (Core.Greeting != null) Core.Greeting.FollowReveal();

            // The clip alone would wipe the week into view as a hard edge; fading it over
            // the back of the travel lets it arrive rather than be uncovered.
            if (_weekFrames != null && _weekFrames.Length > 1 && _weekFrames[1] != null)
                _weekFrames[1].Opacity = t <= 0.35 ? 0 : (t - 0.35) / 0.65;
        }

        /// <summary>Let go of the second week once it has actually folded away - and only
        /// then, so nothing is torn down underneath a transition that is still running.</summary>
        void EndMorph()
        {
            bool two = WeekRows == 2;
            if (Math.Abs(Reveal - (two ? 1 : 0)) > 0.001) return;   // a newer fold owns the clock now
            if (two || _treeWeeks < 2) return;

            SetWeekCount(1);             // the week's rows become garbage here, and not before

            // Seven columns of rows just went away, and the app is by definition idle the
            // instant a fold finishes.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(delegate { MemoryTuning.Trim(false); }));
        }

        /// <summary>Show the week containing a date. Used by the Today button and by the
        /// clock's calendar, so picking a day there scrolls the bar to it.</summary>
        public void GoToWeekOf(DateTime date)
        {
            DateTime target = Board.MondayOf(date);
            if (target == _viewMonday) return;
            int dir = target > _viewMonday ? 1 : -1;
            _viewMonday = target;
            RefreshAll();
            PlayWeekSwitch(dir);
        }

        void ShiftWeek(int delta)
        {
            _viewMonday = _viewMonday.AddDays(7 * delta);
            RefreshAll();
            PlayWeekSwitch(delta);
        }

        /// <summary>The week-change transition. Each week block slides in on its own, a
        /// beat apart, so two weeks read as two things arriving rather than one slab.</summary>
        void PlayWeekSwitch(int direction)
        {
            for (int i = 0; i < _bodyGrid.Children.Count; i++)
            {
                FrameworkElement block = _bodyGrid.Children[i] as FrameworkElement;
                if (block != null) Anim.SlideIn(block, direction * 26, 230, i * 55);
            }
        }

        DateTime MondayOfRow(int week) { return _viewMonday.AddDays(7 * week); }

        /// <summary>The week shown in a row.
        ///
        /// Merely browsing to a week must NOT create it: an empty future week sitting in
        /// the board would later look "already started" and suppress the carry-over of
        /// unfinished tasks. Reads get a throwaway blank; only edits persist one.</summary>
        Week ViewWeek(int week, bool forEdit)
        {
            return WeekOf(MondayOfRow(week), forEdit);
        }

        /// <summary>The row showing a given Monday, or -1 when that week is off-screen.</summary>
        int RowOfMonday(DateTime monday)
        {
            int days = (monday - _viewMonday).Days;
            if (days < 0 || days % 7 != 0) return -1;
            int row = days / 7;
            return row < WeekRows ? row : -1;
        }

        Week WeekOf(DateTime monday, bool forEdit)
        {
            DateTime todayMonday = Board.MondayOf(DateTime.Today);

            if (monday == todayMonday)
                return Core.Data.EnsureCurrentWeek(todayMonday, Core.Config.Rollover);

            Week existing = Core.Data.Get(monday, false);
            if (existing != null) return existing;
            if (forEdit) return Core.Data.Get(monday, true);

            string key = Board.Key(monday);
            Week blank;
            if (!_blanks.TryGetValue(key, out blank))
            {
                // Browsing far enough would otherwise accumulate one of these per week.
                if (_blanks.Count > 8) _blanks.Clear();
                blank = new Week();
                _blanks[key] = blank;
            }
            return blank;
        }

        void UpdateWeekLabel()
        {
            if (_weekLabel == null) return;
            DateTime last = _viewMonday.AddDays(7 * WeekRows - 1);
            _weekLabel.Text = _viewMonday.ToString("MMM d", CultureInfo.CurrentCulture)
                + " – " + last.ToString("MMM d", CultureInfo.CurrentCulture);
        }

        public void RefreshAll()
        {
            UpdateWeekLabel();

            for (int wk = 0; wk < WeekRows; wk++)
                RefreshWeek(wk);
        }

        void RefreshWeek(int week)
        {
            for (int d = 0; d < DayCount; d++)
                RefreshCell(week, d);
        }

        void RefreshCell(int week, int day)
        {
            if (_cells == null || week >= _cells.GetLength(0) || day >= _cells.GetLength(1)) return;
            Cell c = _cells[week, day];
            if (c == null) return;

            Palette p = Core.Skin;
            Week w = ViewWeek(week, false);
            DateTime date = MondayOfRow(week).AddDays(day);
            bool isToday = date == DateTime.Today;

            c.Date.Text = date.Day == 1
                ? date.ToString("d MMM", CultureInfo.CurrentCulture)
                : date.Day.ToString(CultureInfo.CurrentCulture);
            c.Date.Foreground = isToday ? p.DayNameToday : p.DayDate;
            c.Root.Background = isToday ? p.TodayFill : Brushes.Transparent;

            double offset = c.Scroller.VerticalOffset;
            c.List.Children.Clear();

            List<TodoItem> items = w.Days[day];
            for (int i = 0; i < items.Count; i++)
                c.List.Children.Add(MakeRow(week, day, i, items[i]));
            c.List.Children.Add(c.AddRow);

            c.Scroller.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                c.Scroller.ScrollToVerticalOffset(offset);
                UpdateScrollFade(c.Scroller);
            }));
        }

        TaskRow MakeRow(int week, int day, int index, TodoItem item)
        {
            TaskRow row = new TaskRow(Core.Skin, FS, item, week, day, index, MondayOfRow(week).AddDays(day));
            row.Toggled = OnToggle;
            row.EditRequested = OnEdit;
            row.DeleteRequested = OnDelete;
            row.MoveRequested = OnMove;
            return row;
        }

        // ===================================================================== mutations

        void OnToggle(TaskRow row)
        {
            row.Item.Done = !row.Item.Done;
            row.Item.DoneAt = row.Item.Done ? (DateTime?)DateTime.Now : null;
            row.ApplyDoneState(true);      // transition in place - no rebuild, no flicker
            Core.SaveBoardSoon();
        }

        void OnDelete(TaskRow row)
        {
            int week = row.WeekIndex, day = row.Day;
            Anim.CollapseOut(row, 160, delegate
            {
                Week w = ViewWeek(week, false);
                w.Days[day].Remove(row.Item);
                Core.SaveBoardSoon();
                RefreshCell(week, day);
            });
        }

        void OnMove(TaskRow row, DateTime target)
        {
            int week = row.WeekIndex, day = row.Day;
            DateTime targetMonday = Board.MondayOf(target);
            int targetDay = ((int)target.DayOfWeek + 6) % 7;   // Mon=0 .. Sun=6

            Anim.CollapseOut(row, 140, delegate
            {
                Week src = ViewWeek(week, true);
                if (!src.Days[day].Remove(row.Item)) { RefreshCell(week, day); return; }

                // The target can sit in a week that isn't on screen - a Monday picked on
                // a Wednesday belongs to the week ahead.
                Week dst = WeekOf(targetMonday, true);
                dst.Days[targetDay].Add(row.Item);
                Core.SaveBoardSoon();

                RefreshCell(week, day);
                int targetRow = RowOfMonday(targetMonday);
                if (targetRow >= 0)
                {
                    RefreshCell(targetRow, targetDay);
                    PlayLastRowEnter(targetRow, targetDay);
                }
            });
        }

        void ClearCompleted()
        {
            for (int wk = 0; wk < WeekRows; wk++)
            {
                Week w = ViewWeek(wk, false);
                for (int d = 0; d < 7; d++)
                    w.Days[d].RemoveAll(delegate(TodoItem t) { return t.Done; });
            }
            Core.SaveBoardSoon();
            RefreshAll();
            PlayWeekSwitch(0);   // no direction to travel - just a settle-back fade
        }

        // ===================================================================== editing

        void CloseEditor()
        {
            if (_editor != null)
            {
                QuickEdit e = _editor;
                _editor = null;
                try { e.Close(); } catch { }
            }
        }

        void OnEdit(TaskRow row)
        {
            CloseEditor();
            int week = row.WeekIndex, day = row.Day;

            QuickEdit ed = new QuickEdit(Core.Skin, FS, row.Item.Text, "task…");
            _editor = ed;
            ed.Committed = delegate(string text, bool viaEnter)
            {
                _editor = null;
                if (string.IsNullOrEmpty(text))
                {
                    Week w = ViewWeek(week, false);
                    w.Days[day].Remove(row.Item);
                    RefreshCell(week, day);
                }
                else
                {
                    row.Item.Text = text;
                    row.SetText(text);
                }
                Core.SaveBoardSoon();
            };
            ed.Cancelled = delegate { _editor = null; };
            ShowEditorOver(ed, row);
        }

        void StartAdd(int week, int day)
        {
            CloseEditor();
            if (_cells == null || week >= _cells.GetLength(0)) return;
            Cell c = _cells[week, day];
            if (c == null) return;

            QuickEdit ed = new QuickEdit(Core.Skin, FS, "", "new task…");
            _editor = ed;
            ed.Committed = delegate(string text, bool viaEnter)
            {
                _editor = null;
                if (!string.IsNullOrEmpty(text))
                {
                    Week w = ViewWeek(week, true);
                    TodoItem t = new TodoItem();
                    t.Text = text;
                    w.Days[day].Add(t);
                    Core.SaveBoardSoon();
                    RefreshCell(week, day);
                    PlayLastRowEnter(week, day);

                    // Enter keeps the flow going: pop a fresh box for the next task.
                    if (viaEnter)
                        Dispatcher.BeginInvoke(DispatcherPriority.Background,
                            new Action(delegate { StartAdd(week, day); }));
                }
            };
            ed.Cancelled = delegate { _editor = null; };
            ShowEditorOver(ed, c.AddRow);
        }

        void PlayLastRowEnter(int week, int day)
        {
            Cell c = _cells[week, day];
            if (c == null) return;
            for (int i = c.List.Children.Count - 1; i >= 0; i--)
            {
                TaskRow row = c.List.Children[i] as TaskRow;   // skip the trailing add row
                if (row != null) { row.PlayEnter(0); return; }
            }
        }

        void ShowEditorOver(QuickEdit ed, FrameworkElement anchor)
        {
            try
            {
                double scale = DpiScale;
                Point topLeft = anchor.PointToScreen(new Point(0, 0));
                double widthPx = Math.Max(anchor.ActualWidth * scale, 160 * scale);
                ed.ShowAt(topLeft.X, topLeft.Y, widthPx, scale);
            }
            catch { ed.Show(); }
        }

        // ===================================================================== hosting

        double DpiScale
        {
            get
            {
                PresentationSource src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    double m = src.CompositionTarget.TransformToDevice.M11;
                    if (m > 0) return m;
                }
                return 1.0;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            Attach(true);

            // Staying at the bottom of the z-order used to mean walking every top-level
            // window every 3 seconds. The only thing that can bury us is another window
            // being raised, so listen for exactly that instead of polling for it.
            _foregroundProc = OnForegroundChanged;
            _foregroundHook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            // What's left is slow housekeeping: the date rolling over, and re-attaching
            // if Explorer restarted. Neither needs a 3-second heartbeat.
            _watchdog = new DispatcherTimer();
            _watchdog.Interval = TimeSpan.FromSeconds(30);
            _watchdog.Tick += Watchdog;
            _watchdog.Start();

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        Native.WinEventProc _foregroundProc;   // must stay referenced: the hook holds no ref
        IntPtr _foregroundHook = IntPtr.Zero;

        void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time)
        {
            if (_childAttached || _hwnd == IntPtr.Zero) return;
            if (Visibility != Visibility.Visible) return;
            try { DesktopHost.SinkToBottom(_hwnd); }
            catch { }
            Core.RestackWidgets();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { Attach(true); }));
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop || e.Category == UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    _backdropKey = ""; // the wallpaper may have changed under us
                    Attach(true);
                }));
        }

        readonly Dictionary<string, string> _logged = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Write only when this kind of message actually changes. Keyed by kind:
        /// a single-slot check failed because "host:" and "layout:" alternate, so each
        /// looked new every time and the log grew on every pass.</summary>
        void LogOnce(string key, string message)
        {
            string previous;
            if (_logged.TryGetValue(key, out previous) && previous == message) return;
            _logged[key] = message;
            Log.Write(message);
        }

        public void Attach(bool relayout)
        {
            if (_hwnd == IntPtr.Zero) return;

            _mode = DesktopHost.ParseMode(Core.Config.AttachMode);
            _childAttached = false;
            _parent = IntPtr.Zero;

            try
            {
                if (_mode != HostMode.Floating)
                {
                    _parent = DesktopHost.ResolveParent(_mode);
                    _childAttached = DesktopHost.AttachChild(_hwnd, _parent);
                    LogOnce("host", "host: mode=" + _mode + " parent=0x" + _parent.ToInt64().ToString("X")
                        + " ok=" + _childAttached);
                    if (!_childAttached) _parent = IntPtr.Zero;
                }

                if (!_childAttached)
                {
                    DesktopHost.MakeFloating(_hwnd);
                    DesktopHost.SinkToBottom(_hwnd);
                    LogOnce("host", "host: floating" + (_mode == HostMode.Floating ? "" : " (fallback)"));
                }
            }
            catch (Exception ex)
            {
                _childAttached = false;
                _parent = IntPtr.Zero;
                Log.Write("host FAILED: " + ex.Message);
                try { DesktopHost.MakeFloating(_hwnd); DesktopHost.SinkToBottom(_hwnd); }
                catch { }
            }

            if (relayout)
            {
                try { Relayout(); }
                catch (Exception ex) { Log.Write("relayout FAILED: " + ex); }
            }
        }

        /// <summary>Give every week block the same height.
        ///
        /// BarHeight is the height of a *week*, not of the window: two weeks means two
        /// full-height weeks stacked and a taller bar, not one bar's space divided in two.
        /// Only if that won't fit the screen do the weeks give ground, together.
        ///
        /// It is always measured for the envelope - both weeks - whether or not the second
        /// one is showing. A week that changed height depending on how many were out would
        /// make the two ends of a fold disagree about where the first week sits, and the
        /// bar reserves room for both at all times anyway.</summary>
        void MeasureBlocks(double availableDip)
        {
            double chrome = ChromeHeight();
            double gaps = SeparatorHeight;

            double perWeek = Math.Max(40, Core.Config.BarHeight - chrome);
            if (chrome + gaps + perWeek * 2 > availableDip)
                perWeek = Math.Max(40, (availableDip - chrome - gaps) / 2);

            _perWeek = perWeek;
            if (_weekBlocks != null)
                for (int wk = 0; wk < _weekBlocks.Length; wk++)
                    if (_weekBlocks[wk] != null) _weekBlocks[wk].Height = perWeek;
        }

        /// <summary>What the panel stands at showing this many weeks. Both ends of a fold
        /// are read off the blocks' current height, so the panel is the same size at rest
        /// as it is at the end of a transition into that state.</summary>
        double HeightFor(int weeks)
        {
            return ChromeHeight() + SeparatorHeight * (weeks - 1) + _perWeek * weeks;
        }

        double ScreenHeightDip()
        {
            return ScreenForIndex(Core.Config.Monitor).WorkingArea.Height / DpiScale;
        }

        /// <summary>Re-measure the panel, and nothing else.
        ///
        /// A fold changes how much of the panel is showing and not one thing about the
        /// window, so it has no business calling SetWindowPos, re-placing the bar in the
        /// z-order or re-cutting the glass - all of which cost a repaint of the whole bar
        /// at exactly the moment a transition is trying to start.</summary>
        void Remeasure()
        {
            double avail = ScreenHeightDip();
            MeasureBlocks(avail);
            _shellH0 = Math.Min(HeightFor(1), avail);
            _shellH1 = Math.Min(HeightFor(2), avail);
            ApplyReveal();
        }

        public void Relayout()
        {
            if (_hwnd == IntPtr.Zero) return;

            WinForms.Screen scr = ScreenForIndex(Core.Config.Monitor);
            System.Drawing.Rectangle wa = scr.WorkingArea;
            double s = DpiScale;

            int margin = (int)Math.Round(Core.Config.HMargin * s);
            int offset = (int)Math.Round(Core.Config.VOffset * s);

            int w = wa.Width - margin * 2;
            if (w < 240) w = Math.Min(wa.Width, 240);

            _shell.VerticalAlignment = Anchor;
            Remeasure();

            // The window is cut for the envelope - both weeks - at all times, and the panel
            // is pinned inside it to the edge the bar sits against. Folding therefore moves
            // no window at all.
            //
            // It has to be this way round. Resizing a layered window hands it a new origin
            // before WPF has painted anything into the new surface, so for a frame or two
            // Windows shows the OLD picture at the NEW position - the bar visibly jumps and
            // snaps back. That is fine at the end of a settings drag; in the first frame of
            // a transition it is a blink. The part of the window the bar isn't covering is
            // fully transparent, and a layered window passes the mouse straight through
            // transparent pixels, so the reserved space is in nothing's way.
            int height = (int)Math.Round(_shellH1 * s);
            if (height > wa.Height) height = wa.Height;

            int x = wa.X + (wa.Width - w) / 2;
            int y = VerticalPlace(wa, height, offset);

            _placedY = y;
            _placedH = height;

            if (_childAttached)
            {
                // A WS_CHILD window is positioned in its parent's client coordinates.
                DesktopHost.PlaceInParent(_hwnd, _parent, x, y, w, height);
            }
            else
            {
                // Keep WPF's own idea of the position in sync. Otherwise anything that
                // makes WPF re-apply Left/Top (a DPI change, a theme change) flings the
                // window back to wherever WPF thinks it is.
                Left = x / s;
                Top = y / s;
                Width = w / s;
                Height = height / s;

                DesktopHost.PlaceOnScreen(_hwnd, x, y, w, height);
                DesktopHost.SinkToBottom(_hwnd);
            }

            UpdateBackdrop(x, y, w, height, scr.Bounds, s);
            Core.RestackWidgets();

            // Whatever just moved the board moves what hangs off it.
            if (Core.Greeting != null) Core.Greeting.Relayout();

            Native.RECT actual = DesktopHost.RectOf(_hwnd);
            LogOnce("layout", "layout: want=(" + x + "," + y + " " + w + "x" + height + ") dpi=" + s
                + " got=(" + actual.Left + "," + actual.Top + " "
                + (actual.Right - actual.Left) + "x" + (actual.Bottom - actual.Top) + ")"
                + " visible=" + Native.IsWindowVisible(_hwnd));

            if (_firstLayout)
            {
                _firstLayout = false;
                Anim.RiseIn(_shell, 14, 320, 40);
                for (int d = 0; d < DayCount; d++)
                    for (int wk = 0; wk < _treeWeeks; wk++)
                    {
                        if (_headers[wk, d] != null) Anim.RiseIn(_headers[wk, d], 10, 260, 60 + d * 22 + wk * 30);
                        if (_cells[wk, d] != null) Anim.RiseIn(_cells[wk, d].Root, 12, 300, 80 + d * 22 + wk * 30);
                    }
            }
        }

        /// <summary>
        /// Where the panel's top and bottom edges sit on screen, in device pixels, at a
        /// given point in the fold.
        ///
        /// The window is cut for the two-week envelope at all times and the panel is pinned
        /// inside it to the edge the bar sits against, so "where the board ends" is not the
        /// bottom of the window - and for anything but a bottom-anchored bar it moves as a
        /// week folds in or out. Asking for both ends of that travel is what lets the
        /// greeting pill reserve the band it has to move through.
        /// </summary>
        public bool PanelEdges(double reveal, out double top, out double bottom)
        {
            top = 0;
            bottom = 0;
            if (_hwnd == IntPtr.Zero || _placedH <= 0 || _shellH1 <= 0) return false;

            if (reveal < 0) reveal = 0;
            if (reveal > 1) reveal = 1;

            double s = DpiScale;
            double panel = Math.Min((_shellH0 + (_shellH1 - _shellH0) * reveal) * s, _placedH);
            top = _placedY + (_placedH - panel) * AnchorFactor;
            bottom = top + panel;
            return true;
        }

        /// <summary>Where a bar of this height sits on the screen the user asked for.</summary>
        int VerticalPlace(System.Drawing.Rectangle wa, int height, int offset)
        {
            int y;
            if (string.Equals(Core.Config.VAlign, "Top", StringComparison.OrdinalIgnoreCase))
                y = wa.Y + offset;
            else if (string.Equals(Core.Config.VAlign, "Middle", StringComparison.OrdinalIgnoreCase))
                y = wa.Y + (wa.Height - height) / 2;
            else
                y = wa.Bottom - height - offset;

            if (y < wa.Y) y = wa.Y;
            if (y + height > wa.Bottom) y = Math.Max(wa.Y, wa.Bottom - height);
            return y;
        }

        /// <summary>Hold the frosted image still while the panel grows and shrinks over it.
        ///
        /// A brush maps to the box of the element it paints, so left alone it would rescale
        /// the wallpaper on every frame of a fold and the glass would slide about under a
        /// bar that is supposed to be sitting on it. The image is cut for the envelope and
        /// pinned there in absolute coordinates instead, so a shorter bar shows the part of
        /// it that its own rect covers - which is what glass over a fixed wallpaper does.</summary>
        void PinGlass(double shellHeight)
        {
            if (_glassBrush == null || _glassEnvH <= 0) return;
            double slack = Math.Max(0, _glassEnvH - shellHeight);
            Rect viewport = new Rect(0, -slack * AnchorFactor, _glassW, _glassEnvH);
            if (_glassBrush.Viewport != viewport) _glassBrush.Viewport = viewport;
        }

        /// <summary>
        /// Rebuild the frosted backdrop, but only when something it depends on actually
        /// changed - it is far too expensive to run on every layout pass.
        ///
        /// It is cut for the ENVELOPE - the rect a two-week bar would occupy - rather than
        /// for the bar as it currently stands. BlurBackdrop blurs a crop of the wallpaper
        /// and the crop's own edges clamp, so cutting per bar height made the glass change
        /// the moment a week was folded in or out: a pop at each end of the transition, and
        /// a full re-blur to pay for it. Cutting once for the biggest the bar gets and
        /// mapping it in absolute coordinates means folding costs neither.
        /// </summary>
        void UpdateBackdrop(int x, int y, int w, int h, System.Drawing.Rectangle screen, double dpiScale)
        {
            Palette p = Core.Skin;
            Color panel = ColorOf(p.Panel);

            if (!Core.Config.Blur)
            {
                if (_backdropKey != "noblur")
                {
                    _backdropKey = "noblur";
                    _glassBrush = null;
                    _shell.Background = p.Panel;
                    _tint.Background = Brushes.Transparent;
                }
                return;
            }

            string key = string.Join("|", new string[]
            {
                BlurBackdrop.SourceKey(), Core.Config.Theme,
                x.ToString(), y.ToString(), w.ToString(), h.ToString(),
                Core.Config.Opacity.ToString("0.00", CultureInfo.InvariantCulture),
                Core.Config.BlurStrength.ToString("0.0", CultureInfo.InvariantCulture)
            });
            if (key == _backdropKey) return;
            _backdropKey = key;

            ImageBrush blurred = BlurBackdrop.Create(
                x, y, w, h, screen,
                Core.Config.BlurStrength * dpiScale,
                System.Windows.Media.Color.FromRgb(panel.R, panel.G, panel.B), 0.12);

            if (blurred == null)
            {
                // No readable wallpaper - fall back to the flat panel colour.
                _glassBrush = null;
                _shell.Background = p.Panel;
                _tint.Background = Brushes.Transparent;
                return;
            }

            // Left unfrozen on purpose: a fold walks this viewport, and it is the only
            // thing about the glass that a fold touches. The bitmap itself stays frozen and
            // shared.
            _glassBrush = blurred.Clone();
            _glassBrush.Stretch = Stretch.Fill;
            _glassBrush.TileMode = TileMode.None;
            _glassBrush.ViewportUnits = BrushMappingMode.Absolute;
            _glassW = Math.Max(1, w / dpiScale);
            _glassEnvH = Math.Max(1, h / dpiScale);
            PinGlass(_shellH);
            _shell.Background = _glassBrush;

            // Decoding and blurring is the biggest allocation this app makes; don't sit
            // on the garbage afterwards. The pages stay, though - a backdrop is rebuilt
            // when the bar is being resized or themed, which is precisely when the next
            // interaction is a moment away.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(delegate { MemoryTuning.Trim(false); }));

            // The blur is opaque, so the tint on top is what sets the final legibility.
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Core.Config.Opacity * 0.78 * 255)));
            SolidColorBrush tint = new SolidColorBrush(Color.FromArgb(a, panel.R, panel.G, panel.B));
            tint.Freeze();
            _tint.Background = tint;
        }

        /// <summary>Yank the bar to the top of the z-order. If it appears after this, the
        /// bar renders fine and only its stacking is wrong.</summary>
        public void BringToFrontForDebug()
        {
            if (_hwnd == IntPtr.Zero) return;
            Visibility = Visibility.Visible;
            DesktopHost.MakeFloating(_hwnd);
            _childAttached = false;
            _parent = IntPtr.Zero;
            Relayout();
            Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            Native.RECT r = DesktopHost.RectOf(_hwnd);
            Log.Write("debug bring-to-front: rect=(" + r.Left + "," + r.Top + " "
                + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + ") visible="
                + Native.IsWindowVisible(_hwnd));
        }

        static WinForms.Screen ScreenForIndex(int index)
        {
            WinForms.Screen[] all = WinForms.Screen.AllScreens;
            if (index >= 0 && index < all.Length) return all[index];
            return WinForms.Screen.PrimaryScreen;
        }

        void Watchdog(object sender, EventArgs e)
        {
            try
            {
                if (_childAttached && !DesktopHost.IsChildOf(_hwnd, _parent)) Attach(true);
            }
            catch { }

            if (DateTime.Today != _lastSeenDate)
            {
                _lastSeenDate = DateTime.Today;
                DateTime todayMonday = Board.MondayOf(DateTime.Today);
                if (_viewMonday < todayMonday) _viewMonday = todayMonday;
                Core.Data.Prune(todayMonday);
                _blanks.Clear();
                RefreshAll();
                Core.SaveBoardSoon();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_watchdog != null) _watchdog.Stop();
                if (_foregroundHook != IntPtr.Zero)
                {
                    Native.UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
                }
            }
            catch { }
            base.OnClosed(e);
        }
    }
}
