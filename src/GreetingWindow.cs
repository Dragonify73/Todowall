using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>
    /// The greeting pill: a rounded bar floating just under the board, centred on the
    /// screen, wearing the same frosted glass as everything else.
    ///
    /// It is a third desktop-hosted window rather than part of the bar because it hangs
    /// OUTSIDE the panel - the board's own window is cut for the panel and nothing more,
    /// and growing it to hold a strip below would put a transparent band across the top of
    /// the bar in every layout that isn't bottom-anchored.
    ///
    /// It rides the bottom edge of the board: whatever the settings do to the panel -
    /// height, alignment, edge distance - the pill follows, and it keeps following while
    /// the second week folds in and out. That last part is why the window is taller than
    /// the pill: it is cut for the whole band the pill travels through, and the pill moves
    /// INSIDE it. Otherwise every frame of a fold would be a SetWindowPos and a re-blur.
    /// </summary>
    internal class GreetingWindow : Window
    {
        /// <summary>The greeting is set in Bodoni Moda. Bodoni Moda is a webfont, not
        /// a Windows one - the fallbacks walk down the Bodonis that do turn up on a typical
        /// machine and end on Times New Roman, so the worst case is still the same serif the
        /// name is in rather than the default sans.</summary>
        internal static readonly FontFamily GreetingFace =
            new FontFamily("Bodoni Moda, Bodoni MT, Bodoni 72, Times New Roman");

        /// <summary>The name is set in Times New Roman, which ships with Windows - nothing
        /// to fall back to but the generic serif, and it will not come to that.</summary>
        internal static readonly FontFamily NameFace =
            new FontFamily("Times New Roman, Times, serif");

        Grid _root;
        Border _pill;                 // the glass itself: background, outline, corners
        Border _wash;                 // legibility tint on top of it
        TextBlock _label;

        ImageBrush _glassBrush;       // left unfrozen: a fold walks its viewport
        double _pillW, _pillH;        // DIP
        double _windowW, _windowH;    // DIP
        double _pillX;                // where the pill sits across the window
        double _travel;               // how far it slides as the second week folds
        bool _lowFirst;               // true when the one-week position is the higher one

        IntPtr _hwnd = IntPtr.Zero;
        IntPtr _parent = IntPtr.Zero;
        HostMode _mode = HostMode.Floating;
        bool _childAttached;
        string _backdropKey = "";
        string _textKey = "";

        DispatcherTimer _tick;
        Native.WinEventProc _foregroundProc;   // must stay referenced: the hook holds no ref
        IntPtr _foregroundHook = IntPtr.Zero;
        bool _firstLayout = true;

        public GreetingWindow()
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
            Title = "TodoWall Greeting";

            // Parked off-screen until Relayout() places it, so nothing flashes at 0,0.
            Left = -20000;
            Top = -20000;
            Width = 240;
            Height = 60;

            BuildChrome();
            SyncText(true);
        }

        public IntPtr Hwnd { get { return _hwnd; } }

        double FS { get { return Core.Config.FontSize; } }
        double TextSize { get { return FS * 1.5; } }

        /// <summary>Wide and shallow, like the notch: the pill reads as a strip of glass,
        /// not as a box with a sentence in it.</summary>
        double PadX { get { return FS * 2.3; } }
        double PadY { get { return FS * 0.72; } }

        /// <summary>How far under the board the pill sits.</summary>
        double Gap { get { return FS * 0.9; } }

        // ===================================================================== chrome

        void BuildChrome()
        {
            Palette p = Core.Skin;

            _label = new TextBlock();
            // Only the default: SyncText sets a face on each run, and the two halves of the
            // line are deliberately set in different ones.
            _label.FontFamily = NameFace;
            _label.FontSize = TextSize;
            // Same weight as the clock face - this is a caption for the desktop, not body
            // text, and at one line it needs the presence.
            _label.FontWeight = FontWeights.Bold;
            _label.Foreground = p.Text;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.TextAlignment = TextAlignment.Center;
            _label.Margin = new Thickness(PadX, PadY, PadX, PadY);

            _wash = new Border();
            _wash.Background = Brushes.Transparent;

            Grid inner = new Grid();
            inner.Children.Add(_wash);
            inner.Children.Add(_label);

            _pill = new Border();
            // Same reasoning as the bar: the backdrop is a pre-blurred thumbnail stretched
            // over the pill, and bilinear resampling of an already-blurred image is free.
            RenderOptions.SetBitmapScalingMode(_pill, BitmapScalingMode.LowQuality);
            _pill.Background = p.Panel;
            _pill.BorderBrush = p.PanelBorder;
            _pill.BorderThickness = new Thickness(1);
            _pill.CornerRadius = new CornerRadius(Core.Config.CornerRadius);
            _pill.HorizontalAlignment = HorizontalAlignment.Center;
            _pill.VerticalAlignment = VerticalAlignment.Top;
            _pill.Child = inner;
            _pill.ContextMenu = BuildMenu();
            _wash.CornerRadius = _pill.CornerRadius;

            _root = new Grid();
            _root.Children.Add(_pill);
            Content = _root;

            _glassBrush = null;
            _backdropKey = "";   // force a rebuild for the new geometry
            _textKey = "";
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

        // ===================================================================== the words

        /// <summary>Morning until noon, afternoon until five, evening until ten, night after
        /// that - and the small hours stay night rather than becoming an early morning
        /// nobody is having.</summary>
        static string Phrase(DateTime now)
        {
            int h = now.Hour;
            if (h < 5) return "Good night";
            if (h < 12) return "Good morning";
            if (h < 17) return "Good afternoon";
            if (h < 22) return "Good evening";
            return "Good night";
        }

        static string Who()
        {
            string n = Core.Config.UserName;
            return n == null ? "" : n.Trim();
        }

        /// <summary>Re-word the pill. The phrase only changes three times a day, and its
        /// width changes with it, so a change is a full re-place - but the check that
        /// decides is a string compare.</summary>
        void SyncText(bool force)
        {
            string phrase = Phrase(DateTime.Now);
            string name = Who();
            string key = phrase + " " + name;
            if (!force && key == _textKey) return;
            _textKey = key;

            Palette p = Core.Skin;
            _label.Inlines.Clear();

            // The comma belongs to the greeting, not to the name: setting it in the name's
            // bold accent face reads as punctuation someone typed into their own name.
            Run hello = new Run(name.Length > 0 ? phrase + ", " : phrase);
            hello.FontFamily = GreetingFace;
            // Upright and plain: the greeting is the sentence, the name is the emphasis.
            hello.FontStyle = FontStyles.Normal;
            // Bodoni is a didone: hairline strokes and a modest x-height next to Times at
            // the same point size. Nudged up until the two halves read as one line.
            hello.FontSize = TextSize * 1.08;
            hello.FontWeight = FontWeights.Normal;
            _label.Inlines.Add(hello);

            if (name.Length > 0)
            {
                Run who = new Run(name);
                who.FontFamily = NameFace;
                who.FontStyle = FontStyles.Italic;
                who.FontWeight = FontWeights.Bold;
                who.Foreground = p.Accent;
                _label.Inlines.Add(who);
            }

            if (!force) Relayout();
        }

        /// <summary>Re-arm for the top of the next hour: nothing here changes in between.</summary>
        void ArmTick()
        {
            if (_tick == null)
            {
                _tick = new DispatcherTimer();
                _tick.Tick += delegate
                {
                    SyncText(false);
                    ArmTick();
                };
            }
            _tick.Stop();
            DateTime now = DateTime.Now;
            double ms = (60 - now.Minute) * 60000.0 - now.Second * 1000 - now.Millisecond;
            if (ms < 1000) ms = 1000;
            _tick.Interval = TimeSpan.FromMilliseconds(ms);
            _tick.Start();
        }

        // ===================================================================== settings

        public void ApplySettingsChanged()
        {
            BuildChrome();
            SyncText(true);
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
                Log.Write("greeting host FAILED: " + ex.Message);
                try { DesktopHost.MakeFloating(_hwnd); DesktopHost.SinkToBottom(_hwnd); }
                catch { }
            }

            if (relayout)
            {
                try { Relayout(); }
                catch (Exception ex) { Log.Write("greeting relayout FAILED: " + ex); }
            }
        }

        // ===================================================================== layout

        /// <summary>Force a fresh measure of the pill - see the note on the clock's copy:
        /// Measure() is a no-op on an element whose own measure is still valid, and a new
        /// run of text dirties the TextBlock, not the Border around it.</summary>
        static void InvalidateTree(DependencyObject d)
        {
            UIElement e = d as UIElement;
            if (e != null) e.InvalidateMeasure();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) InvalidateTree(VisualTreeHelper.GetChild(d, i));
        }

        void MeasurePill()
        {
            _pill.Margin = new Thickness(0);          // the offset must not be measured in
            InvalidateTree(_pill);
            _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _pillW = Math.Max(40, _pill.DesiredSize.Width);
            _pillH = Math.Max(20, _pill.DesiredSize.Height);

            // The pill borrows the bar's rounding, capped at a half-circle so a shallow
            // strip comes out as a lozenge rather than a rectangle with dented corners.
            CornerRadius r = new CornerRadius(Math.Min(Core.Config.CornerRadius, _pillH / 2));
            _pill.CornerRadius = r;
            _wash.CornerRadius = r;
        }

        /// <summary>Where the pill's top edge belongs at each end of a fold: under the
        /// board if there is room for it there, above it if there isn't.</summary>
        void Anchors(System.Drawing.Rectangle wa, double gap, double ph, out double lo, out double hi)
        {
            double t0 = 0, b0 = 0, t1 = 0, b1 = 0;
            bool ok = false;
            if (Core.Wall != null)
            {
                bool a = Core.Wall.PanelEdges(0, out t0, out b0);
                bool b = Core.Wall.PanelEdges(1, out t1, out b1);
                ok = a && b;
            }

            if (!ok)
            {
                // Nothing to follow yet - sit near the bottom of the screen until the board
                // has been placed and comes back through here.
                lo = hi = wa.Bottom - ph - gap;
                return;
            }

            double belowLo = b0 + gap, belowHi = b1 + gap;
            if (Math.Max(belowLo, belowHi) + ph <= wa.Bottom)
            {
                lo = belowLo;
                hi = belowHi;
                return;
            }

            double aboveLo = t0 - gap - ph, aboveHi = t1 - gap - ph;
            if (Math.Min(aboveLo, aboveHi) >= wa.Y)
            {
                lo = aboveLo;
                hi = aboveHi;
                return;
            }

            // The board fills the screen: tuck the pill against the bottom edge.
            lo = hi = wa.Bottom - ph;
        }

        public void Relayout()
        {
            if (_hwnd == IntPtr.Zero) return;
            Place();
        }

        /// <summary>Follow the board through a fold.
        ///
        /// The window is already cut for the whole band the pill travels through, so this
        /// is a margin and a brush offset - no SetWindowPos, no re-blur, no re-place in the
        /// z-order. It runs on every frame of the board's fold.</summary>
        public void FollowReveal()
        {
            if (_hwnd == IntPtr.Zero || _travel <= 0.5) return;
            ApplyOffset();
        }

        void ApplyOffset()
        {
            if (_pill == null) return;

            double t = Core.Wall != null ? Core.Wall.Reveal : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            double top = _travel * (_lowFirst ? t : 1 - t);
            Thickness m = new Thickness(0, top, 0, 0);
            if (_pill.Margin != m) _pill.Margin = m;

            // Hold the frosted image still while the pill travels over it - the same trick
            // the bar plays with its own glass. A brush maps to the box of the element it
            // paints, so left alone the wallpaper would slide along with the pill.
            if (_glassBrush != null)
            {
                Rect vp = new Rect(-_pillX, -top, _windowW, _windowH);
                if (_glassBrush.Viewport != vp) _glassBrush.Viewport = vp;
            }
        }

        /// <summary>Put the window on the band under (or over) the board, centred, and set
        /// the pill's place inside it.</summary>
        void Place()
        {
            if (_hwnd == IntPtr.Zero) return;

            double s = DpiScale;
            WinForms.Screen scr = ScreenForIndex(Core.Config.Monitor);
            System.Drawing.Rectangle wa = scr.WorkingArea;

            MeasurePill();

            int pw = (int)Math.Round(_pillW * s);
            int ph = (int)Math.Round(_pillH * s);
            if (pw > wa.Width) pw = wa.Width;
            if (ph > wa.Height) ph = wa.Height;
            if (pw < 8) pw = 8;
            if (ph < 8) ph = 8;

            double lo, hi;
            Anchors(wa, Math.Round(Gap * s), ph, out lo, out hi);

            int travel = (int)Math.Round(Math.Abs(hi - lo));
            int h = ph + travel;
            if (h > wa.Height) { h = wa.Height; travel = Math.Max(0, h - ph); }

            int y = (int)Math.Round(Math.Min(lo, hi));
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (y < wa.Y) y = wa.Y;

            int x = wa.X + (wa.Width - pw) / 2;

            // Adopt what we actually got, so the pill stays centred in it.
            _windowW = pw / s;
            _windowH = h / s;
            if (_pillW > _windowW) _pillW = _windowW;
            _pillX = (_windowW - _pillW) / 2;
            _travel = travel / s;
            _lowFirst = lo <= hi;

            ApplyOffset();

            // Placing a window shows it, and the tray's "Hide the bar" may have hidden this
            // one in between. The state above is still worth keeping current; the window
            // itself is left alone until something shows it again, which re-attaches and
            // comes back through here.
            if (Visibility != Visibility.Visible) return;

            if (_childAttached)
            {
                DesktopHost.PlaceInParent(_hwnd, _parent, x, y, pw, h);
            }
            else
            {
                Left = x / s;
                Top = y / s;
                Width = _windowW;
                Height = _windowH;
                DesktopHost.PlaceOnScreen(_hwnd, x, y, pw, h);
                DesktopHost.SinkToBottom(_hwnd);
            }

            UpdateBackdrop(x, y, pw, h, scr.Bounds, s);
            Core.RestackWidgets();

            if (_firstLayout)
            {
                _firstLayout = false;
                Anim.RiseIn(_root, 10, 340, 140);   // arrives just after the bar has landed
            }
        }

        /// <summary>
        /// Same frosted backdrop the bar and the notch use, cut for the whole window - the
        /// band the pill travels through, not the pill as it currently stands.
        ///
        /// BlurBackdrop blurs a downscaled crop of the wallpaper and the crop's own edges
        /// clamp, so cutting per pill position would make the glass change on every frame of
        /// a fold. Cutting once for the band and mapping it in absolute coordinates means a
        /// fold costs nothing here at all: the key doesn't change, so this doesn't run.
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
                    _pill.Background = p.Panel;
                    _wash.Background = Brushes.Transparent;
                }
                return;
            }

            string key = string.Join("|", new string[]
            {
                BlurBackdrop.SourceKey(), Core.Config.Theme,
                x.ToString(), y.ToString(), w.ToString(), h.ToString(),
                Core.Config.Opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Core.Config.BlurStrength.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            });
            if (key == _backdropKey) return;
            _backdropKey = key;

            ImageBrush blurred = BlurBackdrop.Create(
                x, y, w, h, screen,
                Core.Config.BlurStrength * dpiScale,
                Color.FromRgb(panel.R, panel.G, panel.B), 0.12);

            if (blurred == null)
            {
                _glassBrush = null;
                _pill.Background = p.Panel;
                _wash.Background = Brushes.Transparent;
                return;
            }

            // Left unfrozen on purpose: a fold walks this viewport, and it is the only thing
            // about the glass that a fold touches. The bitmap itself stays frozen and shared.
            _glassBrush = blurred.Clone();
            _glassBrush.Stretch = Stretch.Fill;
            _glassBrush.TileMode = TileMode.None;
            _glassBrush.ViewportUnits = BrushMappingMode.Absolute;
            _pill.Background = _glassBrush;
            ApplyOffset();

            // The blur is opaque, so the tint on top is what sets the final legibility.
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Core.Config.Opacity * 0.78 * 255)));
            SolidColorBrush tint = new SolidColorBrush(Color.FromArgb(a, panel.R, panel.G, panel.B));
            tint.Freeze();
            _wash.Background = tint;
        }

        static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Transparent;
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
