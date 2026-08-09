using System;
using System.Windows.Threading;


namespace TodoWall
{
    /// <summary>Shared, process-wide state. Small enough that plumbing it through
    /// constructors would cost more than it's worth.
    /// Field names deliberately differ from their type names (Config/Data/Skin) so
    /// static calls like Board.MondayOf() stay unambiguous.</summary>
    internal static class Core
    {
        public static Settings Config;
        public static Board Data;
        public static Palette Skin;
        public static WallWindow Wall;
        public static ClockWindow Clock;
        public static GreetingWindow Greeting;
        public static Tray TrayIcon;

        static DispatcherTimer _saveTimer;

        public static void Init()
        {
            Config = Settings.Load();
            Data = Store.Load();
            Skin = Palette.Build(Config);
            Anim.Enabled = Config.Animations;
        }

        public static void RebuildSkin()
        {
            Skin = Palette.Build(Config);
        }

        /// <summary>Order the two widgets against each other.
        ///
        /// Both are pinned to the bottom of the z-order, so which of them wins was
        /// whichever sank last - and opening the calendar sinks the clock, dropping it
        /// behind the board at exactly the moment it needs to be over it. The clock is
        /// therefore parked directly above the bar, every time either one is placed.
        /// Neither moves relative to any other window on the desktop.</summary>
        public static void RestackWidgets()
        {
            IntPtr bar = Wall != null ? Wall.Hwnd : IntPtr.Zero;
            IntPtr clock = Clock != null ? Clock.Hwnd : IntPtr.Zero;
            IntPtr greeting = Greeting != null ? Greeting.Hwnd : IntPtr.Zero;
            if (bar == IntPtr.Zero) return;
            try
            {
                // Insert the bar immediately *after* the clock: same place in the stack,
                // one step lower. Then the greeting immediately after the bar, so the three
                // stay a single block wherever the stack puts them.
                if (clock != IntPtr.Zero)
                    Native.SetWindowPos(bar, clock, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                if (greeting != IntPtr.Zero)
                    Native.SetWindowPos(greeting, bar, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>Bring the clock notch into line with the settings: create it, restyle
        /// it, or take it away. Called after anything that could have changed either the
        /// toggle or the look, so callers never have to know which.</summary>
        public static void SyncClock()
        {
            if (Config.ShowClock)
            {
                if (Clock == null)
                {
                    ClockWindow c = new ClockWindow();
                    Clock = c;
                    c.Show();
                    c.Attach(true);
                    RestackWidgets();
                }
                else
                {
                    Clock.ApplySettingsChanged();
                }
            }
            else if (Clock != null)
            {
                ClockWindow c = Clock;
                Clock = null;
                try { c.Close(); } catch { }
            }
        }

        /// <summary>The same for the greeting pill: create it, restyle it, or take it away.
        /// It follows the board's bottom edge, so it is always re-placed after the bar has
        /// been - which <see cref="WallWindow.Relayout"/> does for us.</summary>
        public static void SyncGreeting()
        {
            if (Config.ShowGreeting)
            {
                if (Greeting == null)
                {
                    GreetingWindow g = new GreetingWindow();
                    Greeting = g;
                    g.Show();
                    g.Attach(true);
                    RestackWidgets();
                }
                else
                {
                    Greeting.ApplySettingsChanged();
                }
            }
            else if (Greeting != null)
            {
                GreetingWindow g = Greeting;
                Greeting = null;
                try { g.Close(); } catch { }
            }
        }

        /// <summary>Coalesce rapid edits into a single write.</summary>
        public static void SaveBoardSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer();
                _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
                _saveTimer.Tick += delegate
                {
                    _saveTimer.Stop();
                    Store.Save(Data);
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public static void SaveBoardNow()
        {
            if (_saveTimer != null) _saveTimer.Stop();
            Store.Save(Data);
        }
    }
}
