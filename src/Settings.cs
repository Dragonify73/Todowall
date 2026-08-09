using System;
using System.Globalization;
using System.IO;

namespace TodoWall
{
    internal static class Paths
    {
        public static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoWall");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }
        public static string Board { get { return Path.Combine(Dir, "board.json"); } }
        public static string Config { get { return Path.Combine(Dir, "settings.json"); } }
    }

    internal class Settings
    {
        // Appearance (all sizes are device-independent units; scaled by DPI at layout time)
        public string Theme = "Dark";            // Dark | Light
        public string Accent = "#FF9BE36D";
        public double Opacity = 0.82;            // tint strength over the blurred backdrop
        public double FontSize = 14.5;
        public double BarHeight = 310;           // height of ONE week; the bar grows per week
        public string VAlign = "Middle";         // Top | Middle | Bottom
        public double VOffset = 78;
        public double HMargin = 172;
        public double CornerRadius = 32;

        public int WeekRows = 2;                 // 1 or 2 weeks stacked
        public bool Blur = true;                 // frosted backdrop cut from the wallpaper
        public double BlurStrength = 22;
        public bool Animations = true;

        public bool ShowClock = true;            // the notch on the top edge of the screen
        public bool ShowCalendar = true;         // its pull-down month view

        public bool ShowGreeting = true;         // the pill riding the bottom edge of the board
        public string UserName = "";             // said after the greeting, when set

        // Behaviour
        public string AttachMode = "Floating";    // Floating | DesktopChild | BehindIcons
        public string Rollover = "CarryUnfinished"; // CarryUnfinished | ClearAll | KeepAll
        public bool StartWithWindows = false;

        /// <summary>Off by default: the bar is a per-pixel-alpha window, which WPF
        /// composites in software anyway, so the GPU stack is pure overhead here.</summary>
        public bool HardwareAcceleration = false;
        public int Monitor = 0;                  // index into Screen.AllScreens
        public bool ShowWeekend = true;

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(Paths.Config)) return s;
                J o = J.Parse(File.ReadAllText(Paths.Config));
                if (o == null) return s;

                s.Theme = J.Str(o, "theme", s.Theme);
                s.Accent = J.Str(o, "accent", s.Accent);
                s.Opacity = J.Num(o, "opacity", s.Opacity);
                s.FontSize = J.Num(o, "fontSize", s.FontSize);
                s.BarHeight = J.Num(o, "barHeight", s.BarHeight);
                s.VAlign = J.Str(o, "vAlign", s.VAlign);
                s.VOffset = J.Num(o, "vOffset", s.VOffset);
                s.HMargin = J.Num(o, "hMargin", s.HMargin);
                s.CornerRadius = J.Num(o, "cornerRadius", s.CornerRadius);
                s.WeekRows = (int)J.Num(o, "weekRows", s.WeekRows);
                s.Blur = J.Bool(o, "blur", s.Blur);
                s.BlurStrength = J.Num(o, "blurStrength", s.BlurStrength);
                s.Animations = J.Bool(o, "animations", s.Animations);
                s.ShowClock = J.Bool(o, "showClock", s.ShowClock);
                s.ShowCalendar = J.Bool(o, "showCalendar", s.ShowCalendar);
                s.ShowGreeting = J.Bool(o, "showGreeting", s.ShowGreeting);
                s.UserName = J.Str(o, "userName", s.UserName);
                s.AttachMode = J.Str(o, "attachMode", null);
                if (s.AttachMode == null)
                {
                    // Migrate the old boolean.
                    s.AttachMode = J.Bool(o, "behindIcons", false) ? "BehindIcons" : "Floating";
                }
                s.Rollover = J.Str(o, "rollover", s.Rollover);
                s.StartWithWindows = J.Bool(o, "startWithWindows", s.StartWithWindows);
                s.HardwareAcceleration = J.Bool(o, "hardwareAcceleration", s.HardwareAcceleration);
                s.Monitor = (int)J.Num(o, "monitor", s.Monitor);
                s.ShowWeekend = J.Bool(o, "showWeekend", s.ShowWeekend);
            }
            catch { }
            s.Clamp();
            return s;
        }

        public void Clamp()
        {
            if (Opacity < 0.15) Opacity = 0.15;
            if (Opacity > 1.0) Opacity = 1.0;
            if (FontSize < 8) FontSize = 8;
            if (FontSize > 28) FontSize = 28;
            if (BarHeight < 90) BarHeight = 90;
            if (BarHeight > 1200) BarHeight = 1200;
            if (WeekRows < 1) WeekRows = 1;
            if (WeekRows > 2) WeekRows = 2;
            if (BlurStrength < 0) BlurStrength = 0;
            if (BlurStrength > 80) BlurStrength = 80;
            if (VOffset < 0) VOffset = 0;
            if (HMargin < 0) HMargin = 0;
            if (CornerRadius < 0) CornerRadius = 0;
            if (CornerRadius > 40) CornerRadius = 40;
            if (Monitor < 0) Monitor = 0;
            if (string.IsNullOrEmpty(Accent)) Accent = "#FF9BE36D";
            if (string.IsNullOrEmpty(AttachMode)) AttachMode = "Floating";
            // The greeting is one line on the desktop, not a text field: a name long enough
            // to stretch the pill across the screen is a mistake, not a preference.
            if (UserName == null) UserName = "";
            UserName = UserName.Trim();
            if (UserName.Length > 24) UserName = UserName.Substring(0, 24);
        }

        public void Save()
        {
            try
            {
                Clamp();
                J o = J.Obj();
                o["theme"] = J.Of(Theme);
                o["accent"] = J.Of(Accent);
                o["opacity"] = J.Of(Opacity);
                o["fontSize"] = J.Of(FontSize);
                o["barHeight"] = J.Of(BarHeight);
                o["vAlign"] = J.Of(VAlign);
                o["vOffset"] = J.Of(VOffset);
                o["hMargin"] = J.Of(HMargin);
                o["cornerRadius"] = J.Of(CornerRadius);
                o["weekRows"] = J.Of((double)WeekRows);
                o["blur"] = J.Of(Blur);
                o["blurStrength"] = J.Of(BlurStrength);
                o["animations"] = J.Of(Animations);
                o["showClock"] = J.Of(ShowClock);
                o["showCalendar"] = J.Of(ShowCalendar);
                o["showGreeting"] = J.Of(ShowGreeting);
                o["userName"] = J.Of(UserName);
                o["attachMode"] = J.Of(AttachMode);
                o["rollover"] = J.Of(Rollover);
                o["startWithWindows"] = J.Of(StartWithWindows);
                o["hardwareAcceleration"] = J.Of(HardwareAcceleration);
                o["monitor"] = J.Of((double)Monitor);
                o["showWeekend"] = J.Of(ShowWeekend);
                File.WriteAllText(Paths.Config, o.ToString());
            }
            catch { }
        }

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }
    }
}
