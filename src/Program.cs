using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TodoWall
{
    internal class Program
    {
        static Mutex _single;

        [STAThread]
        public static int Main(string[] args)
        {
            bool createdNew;
            _single = new Mutex(true, "Local\\TodoWall.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("TodoWall is already running — look for its icon in the notification area.",
                    "TodoWall", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            App app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        internal static void LogCrash(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Paths.Dir, "error.log"),
                    DateTime.Now.ToString("u") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    internal class App : Application
    {
        Tray _tray;

        public void InitializeComponent()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Must happen before any FrameworkElement exists, hence first.
            PinLanguageToInvariant();

            Core.Init();

            // Before any menu can be built, so the first one opens already dressed.
            MenuChrome.Install();

            // The bar uses AllowsTransparency, and WPF composites per-pixel-alpha windows
            // in SOFTWARE regardless - yet initialising the render stack still loads the
            // whole GPU driver stack (~150MB of mapped Intel/NVIDIA DLLs here). Opting out
            // of hardware rendering costs this app nothing visually and reclaims all of it.
            // Override with "hardwareAcceleration": true in settings.json if ever needed.
            if (!Core.Config.HardwareAcceleration)
            {
                try
                {
                    System.Windows.Media.RenderOptions.ProcessRenderMode =
                        System.Windows.Interop.RenderMode.SoftwareOnly;
                }
                catch (Exception ex) { Program.LogCrash(ex); }
            }

            DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs ex)
            {
                Program.LogCrash(ex.Exception);
                ex.Handled = true; // a bad paint or a lost desktop handle must not kill the app
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ex)
            {
                Exception inner = ex.ExceptionObject as Exception;
                if (inner != null) Program.LogCrash(inner);
            };

            Log.Write("--- start --- pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " os=" + Environment.OSVersion.Version
                + " monitors=" + System.Windows.Forms.Screen.AllScreens.Length);

            // Keep the Run key honest if the exe moved.
            if (Core.Config.StartWithWindows) StartupService.SetEnabled(true);

            _tray = new Tray();
            Core.TrayIcon = _tray;

            // Over everything, once, on the way in: "Welcome back" and a line for the day,
            // gone on a click. Off in Settings.
            //
            // It goes up FIRST, and the board follows once its fade is over. Both are
            // screen-sized layered windows painted in software on this one thread, and the
            // board's own arrival - first layout, its glass, its rise-in - landing in the
            // same frames as the welcome fade is what made the fade stutter. Nobody sees
            // the board arrive late: the welcome screen is covering it.
            bool welcome = WelcomeWindow.ShowAtStartup();
            if (welcome) After(650, BringUpBoard);
            else BringUpBoard();

            // And again on every unlock: the process outlives a lock, so start-up alone
            // would greet the first sign-in of the day and none of the others.
            WelcomeWindow.WatchSession();

            // Startup churn (JIT, XAML theme dictionaries, the first backdrop render) is
            // the peak; give it back once things settle.
            After(6000, delegate
            {
                MemoryTuning.Trim();
                Log.Write("startup settled, working set " + MemoryTuning.WorkingSetMB + " MB");
            });

            StartIdleTrim();
        }

        /// <summary>The board, the notch and the pill, in that order.</summary>
        void BringUpBoard()
        {
            WallWindow wall = new WallWindow();
            Core.Wall = wall;
            wall.Show();
            wall.Attach(true);

            Core.SyncClock();
            Core.SyncGreeting();

            // Off unless asked for; Sync() is what decides that.
            WallpaperWatch.Sync();

            // Explorer sometimes finishes building its desktop windows slightly after we
            // do; a couple of delayed re-attaches makes cold start reliable.
            Reattach(700);
            Reattach(2500);
        }

        /// <summary>Make WPF's element tree agree with InvariantGlobalization.
        ///
        /// WPF defaults every element's Language to the OS tag ("en-us"), and any binding
        /// that needs a culture calls XmlLanguage.GetSpecificCulture() on it. With ICU
        /// gone that call cannot resolve to a non-neutral culture and throws - which broke
        /// every ComboBox in Settings, since a combo opens by writing IsDropDownOpen back
        /// through exactly such a binding. The exception was caught by the dispatcher
        /// handler, so nothing crashed; the dropdown just silently refused to open.
        ///
        /// CurrentCulture is already invariant in this build, so pinning the tree to match
        /// is consistent rather than a downgrade - the UI is hardcoded English regardless.</summary>
        static void PinLanguageToInvariant()
        {
            try
            {
                FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.Empty));
            }
            catch (Exception ex) { Program.LogCrash(ex); }
        }

        /// <summary>Hand memory back periodically, but never while the user is mid-edit.</summary>
        void StartIdleTrim()
        {
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
            t.Interval = TimeSpan.FromMinutes(5);
            t.Tick += delegate
            {
                if (Core.Wall != null && Core.Wall.IsBusy) return;
                if (Core.Clock != null && Core.Clock.IsBusy) return;
                MemoryTuning.Trim();
            };
            t.Start();
        }

        static void After(int delayMs, Action action)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(delayMs);
            t.Tick += delegate { t.Stop(); action(); };
            t.Start();
        }

        static void Reattach(int delayMs)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(delayMs);
            t.Tick += delegate
            {
                t.Stop();
                if (Core.Wall != null) Core.Wall.Attach(true);
                if (Core.Clock != null) Core.Clock.Attach(true);
                if (Core.Greeting != null) Core.Greeting.Attach(true);
            };
            t.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                WallpaperWatch.Stop();
                WelcomeWindow.StopWatching();
                // Saving here would write board.json - and with it the data folder - straight
                // back out after an uninstall had just deleted them.
                if (!Uninstaller.Removing) Core.SaveBoardNow();
                if (_tray != null) _tray.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
