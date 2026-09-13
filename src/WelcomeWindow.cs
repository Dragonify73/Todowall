using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>
    /// The welcome screen: "Welcome back, name" and a line to start the day on, over the
    /// whole screen, shown when TodoWall starts - which, with start-with-Windows on, is
    /// when you log in - and again each time the session is unlocked, so a sign-in from
    /// the lock screen gets it too (<see cref="WatchSession"/>). A click anywhere takes
    /// it away.
    ///
    /// On unlock it has to be INSTANT, and building it is not: a screen-sized window, the
    /// wallpaper read and blurred, a first paint in software. So it is built while the
    /// PC is locked instead - the lock is the cue - and parked on screen, where the lock
    /// screen covers it anyway, so that unlocking has nothing left to do.
    ///
    /// KNOWN: the constant-alpha fade below does not take effect. WPF strips WS_EX_LAYERED
    /// from any of its windows that are not per-pixel transparent (HwndTarget does it on
    /// WM_STYLECHANGING), so SetLayeredWindowAttributes is ignored and the window appears
    /// and leaves in one step. Fixing that means hosting the content in a layered window
    /// WPF does not own; left as is for now.
    ///
    /// Unlike every other window in the app it sits on TOP of everything rather than under
    /// it, and it is a plain top-level window - never parented to the desktop - because it
    /// has to cover whatever is already open, and it has to take a click.
    ///
    /// Its background is the wallpaper, blurred, cut by <see cref="BlurBackdrop"/> from the
    /// wallpaper FILE the same way the board's glass is. That is what makes it the wallpaper
    /// and nothing else: a screen grab would bake in the icons, the board, and whichever
    /// window Windows restored first, and would need all of them out of the way at exactly
    /// the right moment. Reading the file needs none of that.
    ///
    /// It is also, unlike every other window in the app, OPAQUE - no per-pixel alpha. A
    /// per-pixel-alpha window cannot be handed to the desktop compositor: WPF rasterises
    /// the whole thing on the CPU (this app renders in software on purpose) and copies a
    /// screen's worth of pixels through UpdateLayeredWindow on every frame, and a fade is a
    /// frame's worth of that at each step. At a screen this size the steps land whenever
    /// they finish rather than on a beat, and uneven steps read as jitter.
    ///
    /// So the fade is not WPF's at all. The window carries WS_EX_LAYERED with a CONSTANT
    /// alpha - <see cref="Native.SetLayeredWindowAttributes"/> - which the compositor blends
    /// on the GPU from the window's surface as it already stands. WPF paints the screen once
    /// and is never asked to again while the alpha ramps; the ramp itself is a WPF animation
    /// on a property that draws nothing, chosen for its clock, which ticks at the display's
    /// refresh rate. The only thing WPF repaints during the fade is the words rising into
    /// place, a region a few hundred pixels tall.
    /// </summary>
    internal class WelcomeWindow : Window
    {
        static WelcomeWindow _open;

        Grid _root;
        Border _glass;         // the blurred wallpaper, tinted, stretched over the screen
        StackPanel _words;     // the greeting, the quote, and its author
        TextBlock _hint;

        IntPtr _hwnd = IntPtr.Zero;
        bool _closing;
        DispatcherTimer _closeTimer;
        int _fps = 60;         // the display's refresh rate, once the window knows its screen

        static readonly IEasingFunction OutEase = MakeOut();

        static IEasingFunction MakeOut()
        {
            CubicEase e = new CubicEase();
            e.EasingMode = EasingMode.EaseOut;
            e.Freeze();
            return e;
        }

        /// <summary>The window's constant alpha, 0..1. Animating this is the fade: the
        /// change handler pushes it to the compositor, and nothing in the visual tree is
        /// touched. Not AffectsRender - that is the whole point of it.</summary>
        public static readonly DependencyProperty AlphaProperty = DependencyProperty.Register(
            "Alpha", typeof(double), typeof(WelcomeWindow),
            new PropertyMetadata(0.0, OnAlphaChanged));

        public double Alpha
        {
            get { return (double)GetValue(AlphaProperty); }
            set { SetValue(AlphaProperty, value); }
        }

        static void OnAlphaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WelcomeWindow)d).ApplyAlpha((double)e.NewValue);
        }

        /// <summary>Open it at start-up, if the setting says so. Safe to call whenever: it
        /// does nothing while one is already up.</summary>
        /// <returns>True when it went up, so the caller can hold the board back behind it.</returns>
        public static bool ShowAtStartup()
        {
            if (Core.Config == null || !Core.Config.ShowWelcome) return false;
            ShowNow();
            return _open != null;
        }

        // ===================================================================== unlock

        static bool _watching;

        /// <summary>True while this instance is built and shown but fully transparent,
        /// waiting for the unlock that reveals it.</summary>
        bool _parked;
        System.Drawing.Rectangle _bounds;   // the screen it was placed for

        /// <summary>
        /// Show it again every time the session is unlocked - Win+L, the lid, a sleep that
        /// ended on the lock screen. Start-up alone is not enough: with start-with-Windows
        /// on the process outlives every lock, so "when you log in" would otherwise mean
        /// the first time only, and the sign-in the user actually sees most often - the
        /// one from the lock screen - would get nothing.
        ///
        /// The lock is when the work is done: the window is built and parked transparent
        /// (<see cref="Prepare"/>) so that the unlock has nothing left to do but fade it in
        /// (<see cref="Reveal"/>). SessionSwitch arrives off the UI thread; both go through
        /// the dispatcher.
        /// </summary>
        public static void WatchSession()
        {
            if (_watching) return;
            _watching = true;
            try { SystemEvents.SessionSwitch += OnSessionSwitch; }
            catch (Exception ex) { _watching = false; Log.Write("welcome: session watch FAILED: " + ex.Message); }
        }

        public static void StopWatching()
        {
            if (!_watching) return;
            _watching = false;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        }

        static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Action what;
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    what = Prepare;
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.ConsoleConnect:
                    what = Reveal;
                    break;
                default:
                    return;
            }
            Log.Write("welcome: session " + e.Reason);
            Application app = Application.Current;
            if (app == null) return;
            // Send, not Background: on the unlock there is nothing that should go first.
            app.Dispatcher.BeginInvoke(DispatcherPriority.Send, what);
        }

        /// <summary>The PC has just locked: build the screen now, under the lock screen,
        /// and leave it invisible. Nothing to do if one is already up - the user locked with
        /// the greeting showing, and it is still the greeting when they come back.</summary>
        static void Prepare()
        {
            if (Core.Config == null || !Core.Config.ShowWelcome) return;
            if (_open != null) return;
            try
            {
                WelcomeWindow w = new WelcomeWindow();
                w._parked = true;
                w.ShowActivated = false;    // nothing to activate under a lock screen
                _open = w;
                w.Closed += delegate { if (_open == w) _open = null; };
                w.Show();
            }
            catch (Exception ex)
            {
                _open = null;
                Log.Write("welcome: prepare FAILED: " + ex.Message);
            }
        }

        /// <summary>The session is back: fade in the parked screen, or - if the lock was
        /// never seen, or the screen changed under it - build one now.</summary>
        static void Reveal()
        {
            WelcomeWindow w = _open;
            if (Core.Config == null || !Core.Config.ShowWelcome)
            {
                // Switched off while one was parked: drop it unseen.
                if (w != null && w._parked) { try { w.Close(); } catch { } }
                return;
            }
            if (w == null) { ShowNow(); return; }
            if (!w._parked)
            {
                try { w.Activate(); } catch { }
                return;
            }
            if (w._bounds != ScreenForIndex(Core.Config.Monitor).Bounds)
            {
                // Docked or undocked while locked; the parked one is the wrong shape.
                try { w.Close(); } catch { }
                ShowNow();
                return;
            }
            w.RevealNow();
        }

        /// <summary>Open it regardless of the setting - the preview from Settings.</summary>
        public static void ShowNow()
        {
            if (_open != null)
            {
                try { _open.Activate(); } catch { }
                return;
            }
            try
            {
                WelcomeWindow w = new WelcomeWindow();
                _open = w;
                w.Closed += delegate { if (_open == w) _open = null; };
                w.Show();
            }
            catch (Exception ex)
            {
                _open = null;
                Log.Write("welcome FAILED: " + ex.Message);
            }
        }

        WelcomeWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;     // opaque, and composited like any other window
            ShowInTaskbar = false;
            ShowActivated = true;           // it takes the click, and Escape
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Title = "TodoWall Welcome";
            Cursor = Cursors.Hand;

            // What shows if the first paint is ever seen before the glass is in: the panel
            // colour, not a white window.
            Palette p = Core.Skin;
            Color panel = ColorOf(p.Panel);
            SolidColorBrush ground = new SolidColorBrush(Color.FromRgb(panel.R, panel.G, panel.B));
            ground.Freeze();
            Background = ground;

            // Parked off-screen until it is placed, so nothing flashes at 0,0.
            Left = -20000;
            Top = -20000;
            Width = 400;
            Height = 300;

            BuildChrome();
        }

        double FS { get { return Core.Config.FontSize; } }

        // ===================================================================== chrome

        void BuildChrome()
        {
            Palette p = Core.Skin;

            _glass = new Border();
            // The backdrop is a pre-blurred thumbnail stretched over the screen, so the
            // cheap resampler is the right one: there is nothing sharp in it to lose.
            RenderOptions.SetBitmapScalingMode(_glass, BitmapScalingMode.LowQuality);
            _glass.Background = p.Panel;
            // Rendered once at screen resolution and kept, so the small repaints under the
            // rising words do not re-stretch the thumbnail.
            BitmapCache cache = new BitmapCache();
            cache.RenderAtScale = 1;
            cache.SnapsToDevicePixels = true;
            cache.EnableClearType = false;
            _glass.CacheMode = cache;

            // ---- the words
            _words = new StackPanel();
            _words.HorizontalAlignment = HorizontalAlignment.Center;
            _words.VerticalAlignment = VerticalAlignment.Center;
            _words.Margin = new Thickness(FS * 4, 0, FS * 4, FS * 2);

            string name = Core.Config.UserName == null ? "" : Core.Config.UserName.Trim();

            TextBlock hello = new TextBlock();
            hello.TextAlignment = TextAlignment.Center;
            hello.HorizontalAlignment = HorizontalAlignment.Center;
            hello.TextWrapping = TextWrapping.Wrap;
            hello.Foreground = p.Text;
            hello.FontSize = FS * 4.4;

            // Same split as the pill: the greeting upright in Bodoni, the name in Times
            // bold italic and the accent colour, and the comma belonging to the greeting.
            Run welcome = new Run(name.Length > 0 ? "Welcome back, " : "Welcome back");
            welcome.FontFamily = GreetingWindow.GreetingFace;
            welcome.FontStyle = FontStyles.Normal;
            welcome.FontWeight = FontWeights.Normal;
            welcome.FontSize = FS * 4.4 * 1.08;
            hello.Inlines.Add(welcome);
            if (name.Length > 0)
            {
                Run who = new Run(name);
                who.FontFamily = GreetingWindow.NameFace;
                who.FontStyle = FontStyles.Italic;
                who.FontWeight = FontWeights.Bold;
                who.Foreground = p.Accent;
                hello.Inlines.Add(who);
            }
            _words.Children.Add(hello);

            Quotes.Quote q = Quotes.Next();

            TextBlock quote = new TextBlock();
            quote.Text = "“" + q.Text + "”";
            quote.FontFamily = GreetingWindow.NameFace;
            quote.FontStyle = FontStyles.Italic;
            quote.FontSize = FS * 1.75;
            quote.Foreground = p.Text;
            quote.Opacity = 0.88;
            quote.TextAlignment = TextAlignment.Center;
            quote.TextWrapping = TextWrapping.Wrap;
            quote.HorizontalAlignment = HorizontalAlignment.Center;
            quote.MaxWidth = FS * 46;
            quote.LineHeight = FS * 1.75 * 1.45;
            quote.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            quote.Margin = new Thickness(0, FS * 2.2, 0, 0);
            _words.Children.Add(quote);

            TextBlock by = new TextBlock();
            by.Text = "— " + q.By;
            by.FontFamily = GreetingWindow.NameFace;
            by.FontSize = FS * 1.2;
            by.Foreground = p.Muted;
            by.TextAlignment = TextAlignment.Center;
            by.HorizontalAlignment = HorizontalAlignment.Center;
            by.Margin = new Thickness(0, FS * 0.9, 0, 0);
            _words.Children.Add(by);

            // ---- the way out
            _hint = new TextBlock();
            _hint.Text = "Click anywhere to continue";
            _hint.FontSize = FS * 0.95;
            _hint.Foreground = p.Muted;
            _hint.HorizontalAlignment = HorizontalAlignment.Center;
            _hint.VerticalAlignment = VerticalAlignment.Bottom;
            _hint.Margin = new Thickness(0, 0, 0, FS * 3);
            _hint.Opacity = 0;

            _root = new Grid();
            _root.Background = Brushes.Transparent;   // hit-testable everywhere
            _root.Children.Add(_glass);
            _root.Children.Add(_words);
            _root.Children.Add(_hint);
            Content = _root;
        }

        // ===================================================================== placing

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

            // Fully transparent BEFORE it is shown, so the first frame the compositor puts
            // up is nothing, and the fade starts from there.
            long ex = Native.GetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_LAYERED));
            ApplyAlpha(0);

            WinForms.Screen scr = ScreenForIndex(Core.Config.Monitor);
            System.Drawing.Rectangle b = scr.Bounds;   // the whole screen, taskbar included
            double s = DpiScale;

            Left = b.X / s;
            Top = b.Y / s;
            Width = b.Width / s;
            Height = b.Height / s;
            DesktopHost.PlaceOnScreen(_hwnd, b.X, b.Y, b.Width, b.Height);

            UpdateBackdrop(b, s);

            // The rate the monitor actually draws at; 60 when the driver will not say.
            int hz = Native.RefreshRateOf(scr.DeviceName);
            _fps = hz > 60 ? hz : 60;
            Log.Write("welcome: " + b.Width + "x" + b.Height + " at " + _fps + " fps"
                + (hz == 0 ? " (refresh rate unknown)" : ""));

            _bounds = b;

            if (_parked)
            {
                // Built under the lock screen: painted, placed, invisible. WS_EX_TRANSPARENT
                // as well, so that if anything does reach the desktop before the unlock, a
                // click goes through to whatever is underneath rather than into a window
                // nobody can see.
                ex = Native.GetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TRANSPARENT));
                return;
            }

            RevealNow();
        }

        /// <summary>Everything is in place: bring the window up through the compositor, and
        /// let the words rise into it a beat later. With animations off both land at once.
        ///
        /// The parked case is the one that has to feel instant, so its fade is short: the
        /// window is already painted, the ramp is the compositor's, and the sign-in
        /// transition Windows is playing at the same moment is about that long itself.</summary>
        void RevealNow()
        {
            bool quick = _parked;
            if (_parked)
            {
                _parked = false;
                long ex = Native.GetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongSafe(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~(long)Native.WS_EX_TRANSPARENT));
            }

            FadeWindow(1, quick ? 260 : 420);
            Rise(_words, quick ? 14 : 22, quick ? 360 : 520, quick ? 40 : 140);
            Fade(_hint, 0.9, 600, quick ? 500 : 900);

            try { Activate(); } catch { }
        }

        void ApplyAlpha(double a)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (a < 0) a = 0;
            if (a > 1) a = 1;
            Native.SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(a * 255), Native.LWA_ALPHA);
        }

        /// <summary>
        /// The wallpaper, blurred, for the whole screen. BlurBackdrop works at a sixth of
        /// the target size, so a screen costs no more here than the bar does - a few hundred
        /// pixels across, three box passes.
        ///
        /// The blur is pushed harder than the board's: this is a backdrop for a sentence in
        /// the middle of the screen, not glass over a wallpaper you are meant to still see.
        /// The tint that keeps the words readable goes into the same bitmap - the pill lays
        /// its wash over the glass as a second layer; here there is no reason to.
        /// </summary>
        void UpdateBackdrop(System.Drawing.Rectangle screen, double dpiScale)
        {
            Palette p = Core.Skin;
            Color panel = ColorOf(p.Panel);
            Color solid = Color.FromRgb(panel.R, panel.G, panel.B);

            // The pill's glass is a 0.12 blend baked in, then a wash at 0.62 of the panel
            // opacity laid over it. The same two blends of the same colour, folded into one.
            double wash = Math.Max(0, Math.Min(1, Core.Config.Opacity * 0.62));
            double tint = 1 - 0.88 * (1 - wash);

            ImageBrush blurred = null;
            try
            {
                double radius = Math.Max(Core.Config.BlurStrength, 28) * dpiScale;
                blurred = BlurBackdrop.Create(
                    screen.X, screen.Y, screen.Width, screen.Height, screen,
                    radius, solid, tint);
            }
            catch (Exception ex) { Log.Write("welcome backdrop FAILED: " + ex.Message); }

            if (blurred != null)
            {
                _glass.Background = blurred;
                return;
            }

            // No wallpaper - a solid desktop colour. Wear the desktop's own colour, tinted
            // the same way, so it still reads as "the desktop, softened" rather than a grey
            // card.
            Color d = DesktopColour(solid);
            SolidColorBrush flat = new SolidColorBrush(Color.FromRgb(
                (byte)(d.R * (1 - tint) + solid.R * tint),
                (byte)(d.G * (1 - tint) + solid.G * tint),
                (byte)(d.B * (1 - tint) + solid.B * tint)));
            flat.Freeze();
            _glass.Background = flat;
        }

        /// <summary>The colour Windows paints where there is no wallpaper.</summary>
        static Color DesktopColour(Color fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors", false))
                {
                    string v = k == null ? null : k.GetValue("Background") as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        string[] parts = v.Split(' ');
                        if (parts.Length == 3)
                            return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
                    }
                }
            }
            catch { }
            return fallback;
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

        // ===================================================================== leaving

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Dismiss();
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Space)
            {
                Dismiss();
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>Fade out, then go. Once it has started, further clicks do nothing.</summary>
        void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            IsHitTestVisible = false;

            if (!Anim.Enabled)
            {
                Close();
                return;
            }

            // The whole window at once, through the compositor: words and glass go
            // together, and WPF paints nothing.
            FadeWindow(0, 240);
            _closeTimer = new DispatcherTimer();
            _closeTimer.Interval = TimeSpan.FromMilliseconds(260);
            _closeTimer.Tick += delegate
            {
                _closeTimer.Stop();
                _closeTimer = null;
                try { Close(); } catch { }
            };
            _closeTimer.Start();
        }

        // ===================================================================== motion

        /// <summary>A tween that ticks at the display's rate. Built fresh each time: the
        /// frame rate is an attached property on the timeline, and Anim's shared ones are
        /// frozen at WPF's default.</summary>
        DoubleAnimation Tween(double to, int ms, int delayMs)
        {
            DoubleAnimation a = new DoubleAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = OutEase;
            if (delayMs > 0) a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            Timeline.SetDesiredFrameRate(a, _fps);
            a.Freeze();
            return a;
        }

        /// <summary>The window's alpha, ramped by the compositor - see the class note.</summary>
        void FadeWindow(double to, int ms)
        {
            if (!Anim.Enabled || ms <= 0)
            {
                BeginAnimation(AlphaProperty, null);
                Alpha = to;
                return;
            }
            BeginAnimation(AlphaProperty, Tween(to, ms, 0));
        }

        void Fade(UIElement el, double to, int ms, int delayMs)
        {
            if (el == null) return;
            if (!Anim.Enabled || ms <= 0)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = to;
                return;
            }
            el.BeginAnimation(UIElement.OpacityProperty, Tween(to, ms, delayMs));
        }

        void Rise(FrameworkElement el, double fromY, int ms, int delayMs)
        {
            if (el == null) return;
            if (!Anim.Enabled) { el.Opacity = 1; return; }

            TranslateTransform tt = new TranslateTransform(0, fromY);
            el.RenderTransform = tt;
            el.Opacity = 0;
            tt.BeginAnimation(TranslateTransform.YProperty, Tween(0, ms, delayMs));
            Fade(el, 1, ms, delayMs);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_closeTimer != null) { _closeTimer.Stop(); _closeTimer = null; }
            // A screen-sized blur and its cached copy just became garbage; the board is
            // about to be the only thing left, so hand it back now.
            try { MemoryTuning.Trim(false); } catch { }
            base.OnClosed(e);
        }
    }
}
