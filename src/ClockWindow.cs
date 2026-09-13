using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>
    /// The clock notch: a MacBook-style tab hanging off the top edge of the screen,
    /// wearing exactly the same frosted glass as the bar.
    ///
    /// It is a second desktop-hosted window rather than part of the bar, because the bar
    /// is a full-width panel the user can park anywhere - the notch is always centred on
    /// the top edge, and the two have to be able to move independently.
    ///
    /// The silhouette is drawn rather than composed from Borders: a notch needs two
    /// *inverted* corners at the top, where the sides flare outward to meet the screen
    /// edge, and no combination of CornerRadius produces a concave corner.
    /// </summary>
    internal class ClockWindow : Window
    {
        const double EarMin = 7;      // how far the top corners may flare out
        const double EarMax = 26;
        const double ChevronAngle = 20;

        Grid _root;
        Grid _shell;                  // everything the notch outline contains, Clip'd to it
        Border _glass;                // the blurred wallpaper
        Border _wash;                 // legibility tint on top of it
        Path _edge;                   // 1px outline
        StackPanel _content;
        Border _head;                 // time + date + handle: the click/hover target
        TextBlock _time;
        System.Windows.Documents.Run _lead;      // invisible counterweight to _meridiem
        System.Windows.Documents.Run _timeRun;   // the digits
        System.Windows.Documents.Run _meridiem;  // AM/PM, empty in 24-hour mode
        TextBlock _date;
        Border _handle;               // the dash that becomes an arrow
        RotateTransform _armL, _armR;
        Border _calendarHost;

        // The two shapes the notch travels between, and the window that has to hold both.
        double _shapeW0, _shapeH0;    // folded: just the clock face
        double _shapeW1, _shapeH1;    // unfolded: face plus this month
        double _envW, _envH;          // the largest it ever gets - what the glass is cut for
        double _windowW, _windowH;
        double _ear, _bottom;
        int _rows = 6;                // week rows the current month needs

        IntPtr _hwnd = IntPtr.Zero;
        IntPtr _parent = IntPtr.Zero;
        HostMode _mode = HostMode.Floating;
        bool _childAttached;
        string _backdropKey = "";

        DispatcherTimer _tick;
        Native.WinEventProc _foregroundProc;   // must stay referenced: the hook holds no ref
        IntPtr _foregroundHook = IntPtr.Zero;

        DateTime _lastDate = DateTime.MinValue;
        DateTime _month;               // the month the calendar is showing
        bool _expanded;
        bool _hovered;
        bool _firstLayout = true;

        public ClockWindow()
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
            Title = "TodoWall Clock";

            // Parked off-screen until Relayout() places it, so nothing flashes at 0,0.
            Left = -20000;
            Top = -20000;
            Width = 200;
            Height = 100;

            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            _lastDate = DateTime.Today;

            BuildChrome();
            SyncTime(true);
        }

        public IntPtr Hwnd { get { return _hwnd; } }

        /// <summary>Housekeeping that costs page faults should wait while the calendar is
        /// out or the notch is under the cursor.</summary>
        public bool IsBusy { get { return _expanded || IsMouseOver; } }

        /// <summary>How far the notch is unfolded: 0 is the bare clock, 1 is the calendar
        /// fully out. A dependency property so WPF's own animation clock can drive it -
        /// everything the transition consists of is a function of this one number.</summary>
        public static readonly DependencyProperty RevealProperty =
            DependencyProperty.Register("Reveal", typeof(double), typeof(ClockWindow),
                new PropertyMetadata(0.0, OnRevealChanged));

        public double Reveal
        {
            get { return (double)GetValue(RevealProperty); }
            set { SetValue(RevealProperty, value); }
        }

        static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ClockWindow)d).ApplyShape();
        }

        double FS { get { return Core.Config.FontSize; } }
        double ClockSize { get { return FS * 2.9; } }

        /// <summary>Generous on the sides only. A notch reads as a wide, shallow tab, so
        /// the breathing room goes horizontally - growing it vertically just makes it a
        /// box hanging off the screen.</summary>
        double PadX { get { return FS * 2.6; } }
        double PadTop { get { return FS * 0.55; } }
        double PadBottom { get { return FS * 0.55; } }

        // ===================================================================== chrome

        void BuildChrome()
        {
            Palette p = Core.Skin;

            // The outline is a Clip over full-window layers rather than a Path Fill.
            //
            // A brush on a Shape maps to that shape's bounding box, so animating the
            // outline would rescale the backdrop with it and the wallpaper would slide
            // about as the notch unfolds. Clipping instead leaves the glass pinned to the
            // window - growing the shape reveals more of the same image, which is what
            // frosted glass over a fixed wallpaper actually does.
            _glass = new Border();
            // Same reasoning as the bar: the backdrop is a pre-blurred thumbnail stretched
            // over the shape, and bilinear resampling of an already-blurred image is free.
            RenderOptions.SetBitmapScalingMode(_glass, BitmapScalingMode.LowQuality);
            _glass.Background = p.Panel;

            _wash = new Border();
            _wash.Background = Brushes.Transparent;

            _edge = new Path();
            _edge.Fill = null;
            _edge.Stroke = p.PanelBorder;
            _edge.StrokeThickness = 1;

            _content = new StackPanel();
            // Stretched, not centred, and deliberately so.
            //
            // Centring it would size it to its widest child, which is the head while the
            // notch is folded and the month once it is out. Layout rounding then snaps the
            // panel's offset and the face's offset inside it to device pixels separately,
            // and two roundings of a half-pixel do not always land the same way as one - so
            // the clock and date twitched a pixel sideways the moment the calendar appeared
            // and back again when it left. Spanning the window instead gives the face one
            // fixed box to centre in, whatever is or isn't below it.
            _content.HorizontalAlignment = HorizontalAlignment.Stretch;
            _content.VerticalAlignment = VerticalAlignment.Top;

            _time = new TextBlock();
            _time.FontSize = ClockSize;
            _time.FontWeight = FontWeights.Bold;
            _time.Foreground = p.Text;
            _time.HorizontalAlignment = HorizontalAlignment.Center;
            _time.LineHeight = ClockSize * 1.02;
            _time.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            // Proportional digits make the notch twitch as the minutes roll over; tabular
            // ones keep every time exactly as wide as every other.
            System.Windows.Documents.Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);

            // Runs rather than one string: the meridiem has to be set smaller than the
            // digits it follows, or "PM" at clock size turns the notch into a sign. Setting
            // TextBlock.Text would throw the inlines away, so nothing ever does.
            //
            // _lead is the same text again, invisible, in front. Centring "8:16 PM" as one
            // line puts the middle of the whole string on the centre line, which leaves the
            // digits - the thing the eye actually reads as the clock - sitting half the
            // width of "PM" to the left of it, and the notch looks wrong even though the
            // arithmetic is right. An identical run on the other side balances it exactly,
            // whatever the font does with those two glyphs, and needs no measuring.
            _lead = new System.Windows.Documents.Run();
            _lead.FontSize = ClockSize * 0.42;
            _lead.FontWeight = FontWeights.SemiBold;
            _lead.Foreground = Brushes.Transparent;
            _timeRun = new System.Windows.Documents.Run();
            _meridiem = new System.Windows.Documents.Run();
            _meridiem.FontSize = ClockSize * 0.42;
            _meridiem.FontWeight = FontWeights.SemiBold;
            _time.Inlines.Add(_lead);
            _time.Inlines.Add(_timeRun);
            _time.Inlines.Add(_meridiem);

            _date = new TextBlock();
            _date.FontSize = FS * 0.7;
            _date.FontWeight = FontWeights.SemiBold;
            _date.Foreground = p.Muted;
            _date.HorizontalAlignment = HorizontalAlignment.Center;
            _date.Margin = new Thickness(0, FS * 0.12, 0, 0);

            StackPanel headStack = new StackPanel();
            headStack.Children.Add(_time);
            headStack.Children.Add(_date);
            headStack.Children.Add(BuildHandle());

            _head = new Border();
            // Left stretched, so it fills the window-wide panel and the face sits centred
            // in a box that is the same width folded and unfolded. The band it covers is
            // wider than the notch, but _shell's clip bounds input as well as paint, so the
            // target is exactly the silhouette at this height - the whole tab is the button.
            _head.Background = Brushes.Transparent;   // hit-testable, invisible
            _head.Child = headStack;
            _head.Cursor = Core.Config.ShowCalendar ? Cursors.Hand : Cursors.Arrow;
            _head.MouseEnter += delegate { SetHovered(true); };
            _head.MouseLeave += delegate { SetHovered(false); };
            _head.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ToggleCalendar();
            };
            _content.Children.Add(_head);

            _calendarHost = new Border();
            // Its own width, centred - the panel above it is now window-wide, and the rule
            // along its top edge belongs to the month, not to the whole notch.
            _calendarHost.HorizontalAlignment = HorizontalAlignment.Center;
            _calendarHost.BorderBrush = p.Divider;
            _calendarHost.BorderThickness = new Thickness(0, 1, 0, 0);
            _calendarHost.Margin = new Thickness(0, FS * 0.5, 0, 0);
            _calendarHost.Padding = new Thickness(0, FS * 0.6, 0, FS * 0.1);
            // Empty until it is actually opened - see ShowCalendarTree.
            _calendarHost.Visibility = Visibility.Collapsed;
            _calendarHost.Opacity = 0;
            _content.Children.Add(_calendarHost);

            _shell = new Grid();
            _shell.Children.Add(_glass);
            _shell.Children.Add(_wash);
            _shell.Children.Add(_content);
            _shell.ContextMenu = BuildMenu();

            // Outside the clip, so the 1px outline isn't shaved in half by it.
            _root = new Grid();
            _root.Children.Add(_shell);
            _root.Children.Add(_edge);
            Content = _root;

            _envH = 0;           // text size may have changed; re-measure the envelope
            _backdropKey = "";   // force a rebuild for the new geometry
        }

        /// <summary>The dash under the clock. It is two half-length bars that pivot around
        /// the point they meet, so the flat line genuinely bends into a chevron rather than
        /// being swapped for a different glyph.</summary>
        UIElement BuildHandle()
        {
            Palette p = Core.Skin;
            double span = FS * 1.7;
            double thickness = Math.Max(1.6, FS * 0.13);

            _armL = new RotateTransform(0);
            _armR = new RotateTransform(0);

            Border left = new Border();
            left.Width = span / 2;
            left.Height = thickness;
            left.CornerRadius = new CornerRadius(thickness / 2);
            left.Background = p.Muted;
            left.HorizontalAlignment = HorizontalAlignment.Left;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.RenderTransformOrigin = new Point(1, 0.5);
            left.RenderTransform = _armL;

            Border right = new Border();
            right.Width = span / 2;
            right.Height = thickness;
            right.CornerRadius = new CornerRadius(thickness / 2);
            right.Background = p.Muted;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.RenderTransformOrigin = new Point(0, 0.5);
            right.RenderTransform = _armR;

            Grid arms = new Grid();
            arms.Width = span;
            arms.Height = FS * 0.9;   // room for the arms to swing without reflowing
            arms.Children.Add(left);
            arms.Children.Add(right);

            _handle = new Border();
            _handle.Child = arms;
            _handle.HorizontalAlignment = HorizontalAlignment.Center;
            _handle.Margin = new Thickness(0, FS * 0.3, 0, 0);
            _handle.Opacity = 0.55;
            _handle.Visibility = Core.Config.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;
            return _handle;
        }

        ContextMenu BuildMenu()
        {
            ContextMenu m = new ContextMenu();
            m.Items.Add(MenuItemFor("Settings…", MenuChrome.GlyphSettings,
                delegate { SettingsWindow.ShowSingleton(); }));
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

        // ===================================================================== shape

        /// <summary>
        /// The notch outline, clockwise from the top-left:
        ///
        ///     ────╮                    ╭────      &lt;- inverted (concave) corners
        ///         │                    │
        ///         ╰────────────────────╯          &lt;- ordinary rounded corners
        ///
        /// <paramref name="ear"/> is how far the top corners flare outward, and is what
        /// makes the tab look moulded into the screen edge rather than stuck onto it.
        /// </summary>
        static Geometry Notch(Rect r, double ear, double bottom)
        {
            return Notch(r, ear, bottom, true);
        }

        /// <param name="closeTop">False omits the run along the very top edge and leaves
        /// the figure open, so a stroke of it draws the flares, the sides and the rounded
        /// bottom but no line across the top. The notch is flush with the top of the
        /// screen, where that segment reads as a rule drawn across the display rather than
        /// as an edge of the clock.
        ///
        /// Only the outline asks for this. The clip keeps the closed figure - the glass
        /// still has to be cut to the whole silhouette, top included.</param>
        static Geometry Notch(Rect r, double ear, double bottom, bool closeTop)
        {
            double l = r.Left, t = r.Top, right = r.Right, b = r.Bottom;

            // Degenerate sizes (a one-frame layout, a silly font size) must not throw.
            ear = Math.Max(0.5, Math.Min(ear, r.Width / 2 - 1));
            bottom = Math.Max(0.5, Math.Min(bottom, Math.Min(r.Height - 1, r.Width / 2 - ear - 1)));

            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                if (closeTop)
                {
                    c.BeginFigure(new Point(l, t), true, true);
                    c.LineTo(new Point(right, t), true, false);
                }
                else
                {
                    // Start where the top edge would have finished. The figure is left open,
                    // so the return leg to (l, t) is simply never drawn.
                    c.BeginFigure(new Point(right, t), false, false);
                }
                c.ArcTo(new Point(right - ear, t + ear), new Size(ear, ear), 0, false,
                    SweepDirection.Counterclockwise, true, false);
                c.LineTo(new Point(right - ear, b - bottom), true, false);
                c.ArcTo(new Point(right - ear - bottom, b), new Size(bottom, bottom), 0, false,
                    SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(l + ear + bottom, b), true, false);
                c.ArcTo(new Point(l + ear, b - bottom), new Size(bottom, bottom), 0, false,
                    SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(l + ear, t + ear), true, false);
                c.ArcTo(new Point(l, t), new Size(ear, ear), 0, false,
                    SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        /// <summary>Redraw the outline for the current <see cref="Reveal"/>. This is the
        /// whole transition: the window never moves or resizes, so a frame costs one
        /// nine-segment geometry and a repaint of the band that just appeared - no
        /// SetWindowPos, no re-blur, no re-place in the z-order.</summary>
        void ApplyShape()
        {
            if (_shell == null || _windowW <= 0) return;

            double t = Reveal;
            double w = _shapeW0 + (_shapeW1 - _shapeW0) * t;
            double h = _shapeH0 + (_shapeH1 - _shapeH0) * t;
            double x = (_windowW - w) / 2;

            _shell.Clip = Notch(new Rect(x, 0, w, h), _ear, _bottom);
            _edge.Data = Notch(
                new Rect(x + 0.5, 0.5, Math.Max(2, w - 1), Math.Max(2, h - 1)), _ear, _bottom, false);

            // The clip alone would wipe the month into view as a hard edge; fading it over
            // the back of the travel lets it arrive rather than be uncovered.
            if (_calendarHost != null)
                _calendarHost.Opacity = t <= 0.35 ? 0 : (t - 0.35) / 0.65;
        }

        void AnimateReveal(bool open)
        {
            double to = open ? 1 : 0;
            if (!Anim.Enabled)
            {
                BeginAnimation(RevealProperty, null);
                Reveal = to;
                if (!open) AfterFold();
                return;
            }

            DoubleAnimation a = new DoubleAnimation();
            a.To = to;
            // Out is the gesture people watch; back in just needs to get out of the way.
            a.Duration = new Duration(TimeSpan.FromMilliseconds(open ? 300 : 210));
            CubicEase e = new CubicEase();
            e.EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn;
            a.EasingFunction = e;
            if (!open) a.Completed += delegate { AfterFold(); };
            BeginAnimation(RevealProperty, a);
        }

        // ===================================================================== clock

        void SyncTime(bool force)
        {
            DateTime now = DateTime.Now;

            // 24-hour is the default because its width never depends on the hour and it
            // needs no suffix; 12-hour is offered because plenty of people read a clock
            // that way, and MeasureExtents reserves the room its wider hours need.
            bool h24 = Core.Config.Clock24Hour;
            string time = now.ToString(h24 ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture);
            string suffix = h24 ? "" : " " + now.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant();
            if (force || _timeRun.Text != time) _timeRun.Text = time;
            if (force || _meridiem.Text != suffix) _meridiem.Text = suffix;
            // Mirrored, so the space falls on the outside of the glyphs on both sides.
            string lead = h24 ? "" : suffix.Trim() + " ";
            if (force || _lead.Text != lead) _lead.Text = lead;

            string date = now.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
            if (force || _date.Text != date) _date.Text = date;

            if (now.Date != _lastDate)
            {
                _lastDate = now.Date;
                // Midnight moves the highlighted day. Only matters if the month is
                // actually out - a folded one is rebuilt from scratch when it next opens.
                if (_expanded)
                {
                    FillCalendar();
                    Reshape();
                }
            }
        }

        /// <summary>Re-arm for the next whole minute rather than ticking every second:
        /// nothing on the face changes in between.</summary>
        void ArmTick()
        {
            if (_tick == null)
            {
                _tick = new DispatcherTimer();
                _tick.Tick += delegate
                {
                    SyncTime(false);
                    ArmTick();
                };
            }
            _tick.Stop();
            DateTime now = DateTime.Now;
            double ms = 60000 - (now.Second * 1000 + now.Millisecond);
            if (ms < 250) ms = 250;
            _tick.Interval = TimeSpan.FromMilliseconds(ms);
            _tick.Start();
        }

        // ===================================================================== handle

        void SetHovered(bool on)
        {
            _hovered = on;
            if (!Core.Config.ShowCalendar) return;
            Anim.Fade(_handle, on ? 1.0 : 0.55, 120);
            SwingArms();
        }

        /// <summary>Flat when idle; a chevron pointing the way the panel is about to move
        /// when the notch is under the cursor.</summary>
        void SwingArms()
        {
            double angle = 0;
            if (_hovered) angle = _expanded ? -ChevronAngle : ChevronAngle;
            Swing(_armL, angle);
            Swing(_armR, -angle);
        }

        static void Swing(RotateTransform t, double to)
        {
            if (t == null) return;
            if (!Anim.Enabled)
            {
                t.BeginAnimation(RotateTransform.AngleProperty, null);
                t.Angle = to;
                return;
            }
            DoubleAnimation a = new DoubleAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(170));
            CubicEase e = new CubicEase();
            e.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = e;
            t.BeginAnimation(RotateTransform.AngleProperty, a);
        }

        // ===================================================================== calendar

        /// <summary>
        /// Build the month and make room for it.
        ///
        /// The grid is a hundred-odd elements and the window has to grow to about four
        /// times the area of the folded notch to hold it, and folded is where this thing
        /// spends essentially its whole life. So neither is permanent: the tree is built
        /// on the way open and dropped on the way shut, and the window follows it.
        ///
        /// It still costs nothing during the transition - the window is resized once,
        /// here, before the animation starts, and once more after the fold completes.
        /// In between, the frames are pure clip.
        /// </summary>
        void ShowCalendarTree()
        {
            FillCalendar();
            _calendarHost.Visibility = Visibility.Visible;
            Relayout();
        }

        void DropCalendarTree()
        {
            _calendarHost.Visibility = Visibility.Collapsed;
            _calendarHost.Child = null;
            Relayout();
        }

        void ToggleCalendar()
        {
            if (!Core.Config.ShowCalendar) return;
            _expanded = !_expanded;

            // Always reopen on this month, and with today's dots rather than the ones that
            // were true when it was last shut.
            if (_expanded)
            {
                _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                ShowCalendarTree();
            }

            SwingArms();
            AnimateReveal(_expanded);
        }

        public void CloseCalendar()
        {
            if (!_expanded) return;
            _expanded = false;
            SwingArms();
            AnimateReveal(false);
        }

        /// <summary>Let go of the month once it has actually folded away.</summary>
        void AfterFold()
        {
            if (_expanded) return;          // reopened while it was still closing
            if (_calendarHost.Child == null) return;
            DropCalendarTree();

            // A hundred elements and a surface four times the size just became garbage,
            // and the app is by definition idle the instant a fold finishes.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(delegate { MemoryTuning.Trim(false); }));
        }

        void ShiftMonth(int delta)
        {
            _month = _month.AddMonths(delta);
            FillCalendar();
            Reshape();
            Anim.SlideIn(_calendarHost.Child as FrameworkElement, delta * 18, 200);
        }

        double Cell { get { return FS * 2.15; } }

        void FillCalendar()
        {
            Palette p = Core.Skin;
            double cell = Cell;

            StackPanel panel = new StackPanel();

            // ------------------------------------------------------------ month header
            Grid title = new Grid();
            title.Margin = new Thickness(0, 0, 0, FS * 0.45);

            TextBlock label = new TextBlock();
            label.Text = _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
            label.FontSize = FS * 0.74;
            label.FontWeight = FontWeights.Bold;
            label.Foreground = p.Text;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(label);

            Border prev = Chevron("‹", "Previous month", delegate { ShiftMonth(-1); });
            prev.HorizontalAlignment = HorizontalAlignment.Left;
            title.Children.Add(prev);

            Border next = Chevron("›", "Next month", delegate { ShiftMonth(1); });
            next.HorizontalAlignment = HorizontalAlignment.Right;
            title.Children.Add(next);

            panel.Children.Add(title);

            // ------------------------------------------------------------ the grid
            int lead = ((int)_month.DayOfWeek + 6) % 7;         // Mon=0 .. Sun=6
            int days = DateTime.DaysInMonth(_month.Year, _month.Month);

            // Only as many week rows as this month actually occupies - a fixed six leaves
            // a dead band under most of them, and the notch is sized to its content.
            int rows = (lead + days + 6) / 7;
            _rows = rows;

            Grid grid = new Grid();
            for (int d = 0; d < 7; d++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cell * 0.8) });
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cell) });

            string[] initials = { "M", "T", "W", "T", "F", "S", "S" };
            for (int d = 0; d < 7; d++)
            {
                TextBlock h = new TextBlock();
                h.Text = initials[d];
                h.FontSize = FS * 0.64;
                h.FontWeight = FontWeights.Bold;
                h.Foreground = p.DayName;
                h.HorizontalAlignment = HorizontalAlignment.Center;
                h.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(h, d);
                Grid.SetRow(h, 0);
                grid.Children.Add(h);
            }

            for (int i = 0; i < days; i++)
            {
                DateTime date = _month.AddDays(i);
                int slot = lead + i;
                UIElement day = DayCell(date, cell);
                Grid.SetColumn(day, slot % 7);
                Grid.SetRow(day, slot / 7 + 1);
                grid.Children.Add(day);
            }

            panel.Children.Add(grid);
            _calendarHost.Child = panel;
        }

        UIElement DayCell(DateTime date, double cell)
        {
            Palette p = Core.Skin;
            bool isToday = date == DateTime.Today;

            TextBlock num = new TextBlock();
            num.Text = date.Day.ToString(CultureInfo.InvariantCulture);
            num.FontSize = FS * 0.78;
            num.FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal;
            num.HorizontalAlignment = HorizontalAlignment.Center;
            num.VerticalAlignment = VerticalAlignment.Center;
            num.Margin = new Thickness(0, 0, 0, cell * 0.11);
            num.Foreground = isToday
                ? new SolidColorBrush(Color.FromRgb(20, 22, 28))   // the accents are all pale
                : (date.Month == _month.Month ? p.Text : p.Muted);

            Grid inner = new Grid();
            inner.Children.Add(num);

            // A dot for a day that has tasks - the calendar is part of the board, not a
            // generic month view.
            int open, total;
            TaskCount(date, out open, out total);
            if (total > 0)
            {
                Ellipse dot = new Ellipse();
                dot.Width = dot.Height = Math.Max(3, FS * 0.24);
                dot.Fill = open > 0 ? p.Accent : p.TextDone;
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.VerticalAlignment = VerticalAlignment.Bottom;
                dot.Margin = new Thickness(0, 0, 0, cell * 0.14);
                if (isToday) dot.Fill = new SolidColorBrush(Color.FromArgb(150, 20, 22, 28));
                inner.Children.Add(dot);
            }

            SolidColorBrush bg = new SolidColorBrush(isToday ? ColorOf(p.Accent) : Colors.Transparent);

            Border b = new Border();
            b.Child = inner;
            b.Width = cell;
            b.Height = cell;
            b.CornerRadius = new CornerRadius(cell / 2);
            b.Background = bg;
            b.Cursor = Cursors.Hand;
            b.ToolTip = total > 0
                ? total + (total == 1 ? " task" : " tasks") + " — click to show that week"
                : "Show that week on the bar";

            if (!isToday)
            {
                b.MouseEnter += delegate { Anim.Tint(bg, ColorOf(p.HoverRow), Anim.HoverIn); };
                b.MouseLeave += delegate { Anim.Tint(bg, Colors.Transparent, Anim.HoverOut); };
            }
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                Anim.Pop(b, 0.88, 180);
                if (Core.Wall != null) Core.Wall.GoToWeekOf(date);
            };
            return b;
        }

        static void TaskCount(DateTime date, out int open, out int total)
        {
            open = 0;
            total = 0;
            if (Core.Data == null) return;
            Week w = Core.Data.Get(Board.MondayOf(date), false);
            if (w == null) return;
            foreach (TodoItem t in w.Days[((int)date.DayOfWeek + 6) % 7])
            {
                total++;
                if (!t.Done) open++;
            }
        }

        Border Chevron(string glyph, string tip, Action onClick)
        {
            Palette p = Core.Skin;
            Color mutedColor = ColorOf(p.Muted);
            Color textColor = ColorOf(p.Text);

            SolidColorBrush fg = new SolidColorBrush(mutedColor);

            TextBlock tb = new TextBlock();
            tb.Text = glyph;
            tb.FontSize = FS * 1.0;
            tb.Foreground = fg;
            tb.VerticalAlignment = VerticalAlignment.Center;

            Border b = new Border();
            b.Child = tb;
            b.Padding = new Thickness(FS * 0.4, 0, FS * 0.4, 0);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = tip;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.MouseEnter += delegate { Anim.Tint(fg, textColor, Anim.HoverIn); };
            b.MouseLeave += delegate { Anim.Tint(fg, mutedColor, Anim.HoverOut); };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                onClick();
            };
            return b;
        }

        static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Transparent;
        }

        // ===================================================================== settings

        public void ApplySettingsChanged()
        {
            _expanded = _expanded && Core.Config.ShowCalendar;
            BuildChrome();
            if (_expanded) FillCalendar();
            _calendarHost.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            SyncTime(true);

            // Land on the end state rather than replaying the transition: this runs on
            // every drag of a settings slider.
            BeginAnimation(RevealProperty, null);
            Reveal = _expanded ? 1 : 0;

            SwingArms();
            Relayout();
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
            ArmTick();

            _foregroundProc = OnForegroundChanged;
            _foregroundHook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

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
                    _backdropKey = "";   // the wallpaper may have changed under us
                    Attach(true);
                }));
        }

        /// <summary>Drop the cached glass and cut it again from whatever the wallpaper is
        /// now. Nothing else about the window changes - see <see cref="WallpaperWatch"/>.</summary>
        public void RefreshBackdrop()
        {
            _backdropKey = "";
            Relayout();
        }

        public void Attach(bool relayout)
        {
            if (_hwnd == IntPtr.Zero) return;

            _mode = DesktopHost.Mode;
            _childAttached = false;
            _parent = IntPtr.Zero;

            try
            {
                if (_mode != HostMode.Floating)
                {
                    _parent = DesktopHost.ResolveParent(_mode);
                    _childAttached = DesktopHost.AttachChild(_hwnd, _parent);
                    if (!_childAttached) _parent = IntPtr.Zero;
                }

                if (!_childAttached)
                {
                    DesktopHost.MakeFloating(_hwnd);
                    DesktopHost.SinkToBottom(_hwnd);
                }
            }
            catch (Exception ex)
            {
                _childAttached = false;
                _parent = IntPtr.Zero;
                Log.Write("clock host FAILED: " + ex.Message);
                try { DesktopHost.MakeFloating(_hwnd); DesktopHost.SinkToBottom(_hwnd); }
                catch { }
            }

            if (relayout)
            {
                try { Relayout(); }
                catch (Exception ex) { Log.Write("clock relayout FAILED: " + ex); }
            }
        }

        /// <summary>
        /// Work out the folded and unfolded outlines, and how big a window has to be to
        /// hold the larger of them.
        ///
        /// The window is sized for the unfolded shape at all times, even while folded -
        /// that is what lets the transition be a clip animation instead of a per-frame
        /// SetWindowPos. The uncovered part of the window is fully transparent, and a
        /// layered window passes mouse input straight through transparent pixels, so the
        /// extra area is not in anyone's way.
        /// </summary>
        void MeasureExtents()
        {
            // Measure the widest face this clock can ever show, not the one it happens to
            // be showing. In 12-hour mode the hour swings between one and two digits, so
            // sizing to the current time would mean re-placing the window - visibly moving
            // the notch on the screen - at 9:59 to make room for 10:00. Tabular numerals
            // make every digit the same width, so a two-digit hour is the whole story.
            string time = _timeRun.Text, suffix = _meridiem.Text, lead = _lead.Text;
            if (!Core.Config.Clock24Hour)
            {
                // The meridiem is pinned too: nothing re-measures at noon, so if AM and PM
                // are not the same width in this font the shape has to already fit both.
                _timeRun.Text = "12:00";
                _meridiem.Text = " PM";
                _lead.Text = "PM ";
            }

            _content.Margin = new Thickness(0, PadTop, 0, 0);
            InvalidateTree(_content);
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double bodyW0 = Math.Max(FS * 11, _head.DesiredSize.Width) + PadX * 2;
            double bodyH0 = PadTop + _head.DesiredSize.Height + PadBottom;

            // The notch borrows the bar's corner rounding so the two read as one widget.
            // Both radii come from the folded size - the tighter of the two - so the
            // corners stay put instead of morphing as it opens.
            _bottom = Math.Max(6, Math.Min(Core.Config.CornerRadius,
                Math.Min(bodyH0 * 0.5, bodyW0 * 0.4)));
            _ear = Math.Max(EarMin, Math.Min(EarMax, _bottom * 0.62));

            _shapeW0 = bodyW0 + _ear * 2;
            _shapeH0 = bodyH0;

            bool unfolds = _calendarHost.Visibility == Visibility.Visible;
            // DesiredSize already carries the top pad, since that is _content's margin.
            _shapeW1 = unfolds
                ? Math.Max(_shapeW0, _content.DesiredSize.Width + PadX * 2 + _ear * 2) : _shapeW0;
            _shapeH1 = unfolds
                ? Math.Max(_shapeH0, _content.DesiredSize.Height + PadBottom) : _shapeH0;

            // The envelope: the largest this thing ever gets. Seven fixed columns make the
            // unfolded WIDTH pure arithmetic on the text size, so it is known even with no
            // month built; the height has to be measured, so it is measured once and kept.
            _envW = Core.Config.ShowCalendar
                ? Math.Max(_shapeW0, 7 * Cell + PadX * 2 + _ear * 2) : _shapeW0;
            if (unfolds)
            {
                // Reserve the tallest month there can be. Otherwise February would shrink
                // the window and March would have to grow it again - a resize mid-browse
                // for nothing the eye gains.
                _envH = _shapeH1 + (6 - _rows) * Cell;
            }
            else if (Core.Config.ShowCalendar && _envH <= 0)
            {
                _envH = MeasureEnvelopeHeight();
            }
            if (!Core.Config.ShowCalendar) _envH = _shapeH0;

            // Width never changes, so the notch never shifts sideways and the glass never
            // has to be re-cut horizontally; only the height follows the fold, and the top
            // edge is pinned, so nothing on the face moves when it does.
            _windowW = _envW;
            _windowH = unfolds ? _envH : _shapeH0;

            // Back to the real time. Every extent above is a stored number now, so the
            // measure this dirties costs nothing but the next layout pass.
            _timeRun.Text = time;
            _meridiem.Text = suffix;
            _lead.Text = lead;
        }

        /// <summary>Build a month purely to find out how tall the unfolded notch is, then
        /// throw it away again. Runs once per chrome build, not per fold.</summary>
        double MeasureEnvelopeHeight()
        {
            FillCalendar();
            _calendarHost.Visibility = Visibility.Visible;
            InvalidateTree(_content);
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double h = Math.Max(_shapeH0, _content.DesiredSize.Height + PadBottom)
                     + (6 - _rows) * Cell;

            _calendarHost.Visibility = Visibility.Collapsed;
            _calendarHost.Child = null;
            InvalidateTree(_content);
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return h;
        }

        /// <summary>
        /// Force a fresh measure of the whole face.
        ///
        /// Measure() is a no-op on an element whose own measure is still valid, and WPF
        /// marks only the element that actually changed: a new month grid dirties the
        /// Border it was dropped into, not the StackPanel above it, and a longer date
        /// dirties the TextBlock, not the Border around it. Measuring the root would then
        /// hand back last month's height - which is exactly what it did, growing the
        /// window for a month that needs one row fewer. Invalidating the subtree first
        /// costs one walk of a few dozen elements and makes the answer right every time.
        /// </summary>
        static void InvalidateTree(DependencyObject d)
        {
            UIElement e = d as UIElement;
            if (e != null) e.InvalidateMeasure();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) InvalidateTree(VisualTreeHelper.GetChild(d, i));
        }

        /// <summary>Re-measure after the content changed. Only touches the window itself
        /// if the outer envelope actually moved - folding and unfolding does not.</summary>
        void Reshape()
        {
            double w = _windowW, h = _windowH;
            MeasureExtents();
            if (Math.Abs(w - _windowW) > 0.5 || Math.Abs(h - _windowH) > 0.5) Place();
            else ApplyShape();
        }

        public void Relayout()
        {
            if (_hwnd == IntPtr.Zero) return;
            MeasureExtents();
            Place();
        }

        /// <summary>Put the window on the top edge of the screen, centred, and cut the
        /// current notch out of it.</summary>
        void Place()
        {
            if (_hwnd == IntPtr.Zero) return;

            double s = DpiScale;
            WinForms.Screen scr = ScreenForIndex(Core.Config.Monitor);
            System.Drawing.Rectangle wa = scr.WorkingArea;

            int pw = (int)Math.Round(_windowW * s);
            int ph = (int)Math.Round(_windowH * s);
            if (pw > wa.Width) pw = wa.Width;
            if (ph > wa.Height) ph = wa.Height;
            if (pw < 8) pw = 8;
            if (ph < 8) ph = 8;

            int px = wa.X + (wa.Width - pw) / 2;
            int py = wa.Y;

            // Adopt what we actually got, so the shape stays centred in it.
            _windowW = pw / s;
            if (_envW > _windowW) _envW = _windowW;
            if (_shapeW1 > _windowW) _shapeW1 = _windowW;
            if (_shapeW0 > _windowW) _shapeW0 = _windowW;
            _windowH = ph / s;
            if (_shapeH1 > _envH) _shapeH1 = _envH;
            if (_shapeH0 > _windowH) _shapeH0 = _windowH;

            ApplyShape();

            // Placing a window shows it, and folding now finishes ~200ms after the click
            // that started it - long enough for the tray's "Hide the bar" to have hidden
            // this one in between. The state above is still worth keeping current; the
            // window itself is left alone until something shows it again, which re-attaches
            // and comes back through here.
            if (Visibility != Visibility.Visible) return;

            if (_childAttached)
            {
                DesktopHost.PlaceInParent(_hwnd, _parent, px, py, pw, ph);
            }
            else
            {
                Left = px / s;
                Top = py / s;
                Width = _windowW;
                Height = _windowH;
                DesktopHost.PlaceOnScreen(_hwnd, px, py, pw, ph);
                DesktopHost.SinkToBottom(_hwnd);
            }

            UpdateBackdrop(px, py, pw, (int)Math.Round(_envH * s), scr.Bounds, s);
            Core.RestackWidgets();

            if (_firstLayout)
            {
                _firstLayout = false;
                Anim.RiseIn(_root, -12, 340, 60);   // slides down out of the screen edge
            }
        }

        /// <summary>
        /// Same frosted backdrop the bar uses - but cut for the ENVELOPE, not for the
        /// window as it currently stands.
        ///
        /// BlurBackdrop blurs a downscaled crop of the wallpaper, so the crop's own edges
        /// clamp: at this blur strength a folded notch is only ~16 rows tall once scaled,
        /// less than two blur radii, and comes out visibly flatter than the same strip of
        /// wallpaper blurred inside the taller unfolded crop. Cutting per window size
        /// therefore made the glass change the instant the window resized - a pop at the
        /// start of the unfold and the end of the fold.
        ///
        /// So it is cut once for the biggest the notch gets and mapped in absolute
        /// coordinates. A shorter window then shows the top of that same image rather than
        /// a differently-blurred one, which is both correct and free: the key stops
        /// changing on a fold, so this stops running on one.
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
                    _glass.Background = p.Panel;
                    _wash.Background = Brushes.Transparent;
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
                Color.FromRgb(panel.R, panel.G, panel.B), 0.12);

            if (blurred == null)
            {
                _glass.Background = p.Panel;
                _wash.Background = Brushes.Transparent;
                return;
            }

            // Pin it to the envelope rather than to the element's own box, which is what
            // an ImageBrush would otherwise stretch to.
            ImageBrush pinned = blurred.Clone();       // shares the frozen bitmap
            pinned.Stretch = Stretch.Fill;
            pinned.TileMode = TileMode.None;
            pinned.ViewportUnits = BrushMappingMode.Absolute;
            pinned.Viewport = new Rect(0, 0, Math.Max(1, w / dpiScale), Math.Max(1, h / dpiScale));
            pinned.Freeze();
            _glass.Background = pinned;

            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Core.Config.Opacity * 0.78 * 255)));
            SolidColorBrush tint = new SolidColorBrush(Color.FromArgb(a, panel.R, panel.G, panel.B));
            tint.Freeze();
            _wash.Background = tint;
        }

        static WinForms.Screen ScreenForIndex(int index)
        {
            WinForms.Screen[] all = WinForms.Screen.AllScreens;
            if (index >= 0 && index < all.Length) return all[index];
            return WinForms.Screen.PrimaryScreen;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_tick != null) { _tick.Stop(); _tick = null; }
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
