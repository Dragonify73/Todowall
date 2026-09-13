using System;
using System.Windows.Threading;

namespace TodoWall
{
    /// <summary>
    /// An optional poll for wallpaper changes.
    ///
    /// The widgets are frosted with a slice of the wallpaper FILE, so a new wallpaper
    /// means the glass has to be cut again. Normally they hear about that: Windows raises
    /// UserPreferenceChanged(Desktop) when the picture is changed through Settings, and
    /// every widget re-cuts on it. What it does not reliably announce is the picture
    /// changing on its own - a slideshow rolling over, Spotlight fetching tomorrow's
    /// image, a wallpaper app rewriting the same file behind the same path - and there the
    /// board is left wearing yesterday's glass until something else moves it.
    ///
    /// So this is a fallback rather than the mechanism, which is why it is off by default:
    /// most people never need it, and nobody should pay for a timer they do not.
    ///
    /// The check itself is a registry read and one file stat, compared against the same
    /// key <see cref="BlurBackdrop.SourceKey"/> gives the widgets. When it has not moved -
    /// which is nearly every tick - the whole thing costs a string compare and no repaint.
    /// </summary>
    internal static class WallpaperWatch
    {
        static DispatcherTimer _timer;
        static string _key;

        /// <summary>Bring the timer into line with the settings: start it, re-arm it at a
        /// new interval, or stop it. Safe to call whenever either setting might have
        /// changed, so callers never have to work out which happened.</summary>
        public static void Sync()
        {
            Settings cfg = Core.Config;
            // No glass means nothing for a new wallpaper to change, so the poll stops with
            // the frosted backdrop as well as with its own switch.
            if (cfg == null || !cfg.WatchWallpaper || !cfg.Blur) { Stop(); return; }

            TimeSpan interval = TimeSpan.FromMinutes(cfg.WatchMinutes);

            if (_timer == null)
            {
                // Background priority: a wallpaper that changed a moment ago can wait for
                // the board to finish whatever it is drawing.
                _timer = new DispatcherTimer(DispatcherPriority.Background);
                _timer.Tick += OnTick;
                // Whatever is on screen now is by definition current, so the first tick
                // has something to compare against and does not repaint on startup.
                _key = SafeKey();
            }
            else if (_timer.Interval == interval && _timer.IsEnabled)
            {
                return;      // already running exactly like this
            }

            _timer.Stop();
            _timer.Interval = interval;
            _timer.Start();
        }

        public static void Stop()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
            _key = null;
        }

        static void OnTick(object sender, EventArgs e)
        {
            // Re-cutting the glass costs a wallpaper decode and three blur passes per
            // widget. Mid-edit or mid-hover is the one moment that is worth avoiding, and
            // the next tick is only a minute away.
            if (Core.Wall != null && Core.Wall.IsBusy) return;
            if (Core.Clock != null && Core.Clock.IsBusy) return;

            string key = SafeKey();
            if (key == _key) return;
            _key = key;

            Log.Write("wallpaper changed, re-cutting the glass");
            Refresh();
        }

        /// <summary>Re-cut every widget that is currently up. Each one throws its cached
        /// key away first, so this holds even when only the file behind an unchanged path
        /// was rewritten.</summary>
        static void Refresh()
        {
            try { if (Core.Wall != null) Core.Wall.RefreshBackdrop(); }
            catch (Exception ex) { Log.Write("wallpaper refresh (bar) FAILED: " + ex.Message); }
            try { if (Core.Clock != null) Core.Clock.RefreshBackdrop(); }
            catch (Exception ex) { Log.Write("wallpaper refresh (clock) FAILED: " + ex.Message); }
            try { if (Core.Greeting != null) Core.Greeting.RefreshBackdrop(); }
            catch (Exception ex) { Log.Write("wallpaper refresh (greeting) FAILED: " + ex.Message); }

            // Three wallpaper decodes and nine blur passes just became garbage.
            MemoryTuning.Trim(false);
        }

        static string SafeKey()
        {
            try { return BlurBackdrop.SourceKey(); }
            catch { return null; }
        }
    }
}
