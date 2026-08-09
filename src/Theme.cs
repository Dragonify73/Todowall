using System;
using System.Windows.Media;

namespace TodoWall
{
    /// <summary>Resolved brushes for the current settings. Rebuilt whenever settings change.</summary>
    internal class Palette
    {
        public Brush Panel;          // bar background
        public Brush PanelBorder;
        public Brush TodayFill;      // today's column wash
        public Brush Divider;
        public Brush DayName;
        public Brush DayNameToday;
        public Brush DayDate;
        public Brush Text;
        public Brush TextDone;
        public Brush Muted;
        public Brush CheckBorder;
        public Brush CheckHover;
        public Brush Accent;
        public Brush AccentDim;
        public Brush HoverRow;
        public Color AccentColor;
        public bool IsDark;

        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                object o = ColorConverter.ConvertFromString(hex);
                if (o is Color) return (Color)o;
            }
            catch { }
            return fallback;
        }

        static Brush Frozen(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static Color Rgba(byte a, byte r, byte g, byte b)
        {
            return Color.FromArgb(a, r, g, b);
        }

        public static Palette Build(Settings s)
        {
            Palette t = new Palette();
            t.IsDark = !string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            t.AccentColor = Parse(s.Accent, Rgba(255, 111, 177, 255));
            t.Accent = Frozen(t.AccentColor);
            t.AccentDim = Frozen(Rgba(70, t.AccentColor.R, t.AccentColor.G, t.AccentColor.B));

            byte panelAlpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s.Opacity * 255)));

            if (t.IsDark)
            {
                t.Panel = Frozen(Rgba(panelAlpha, 18, 20, 26));
                t.PanelBorder = Frozen(Rgba(46, 255, 255, 255));
                t.TodayFill = Frozen(Rgba(26, t.AccentColor.R, t.AccentColor.G, t.AccentColor.B));
                t.Divider = Frozen(Rgba(28, 255, 255, 255));
                t.DayName = Frozen(Rgba(150, 235, 240, 250));
                t.DayNameToday = t.Accent;
                t.DayDate = Frozen(Rgba(90, 235, 240, 250));
                t.Text = Frozen(Rgba(238, 240, 244, 250));
                t.TextDone = Frozen(Rgba(90, 235, 240, 250));
                t.Muted = Frozen(Rgba(110, 235, 240, 250));
                t.CheckBorder = Frozen(Rgba(95, 235, 240, 250));
                t.CheckHover = Frozen(Rgba(170, 235, 240, 250));
                t.HoverRow = Frozen(Rgba(20, 255, 255, 255));
            }
            else
            {
                t.Panel = Frozen(Rgba(panelAlpha, 250, 250, 252));
                t.PanelBorder = Frozen(Rgba(40, 0, 0, 0));
                t.TodayFill = Frozen(Rgba(30, t.AccentColor.R, t.AccentColor.G, t.AccentColor.B));
                t.Divider = Frozen(Rgba(26, 0, 0, 0));
                t.DayName = Frozen(Rgba(160, 20, 24, 32));
                t.DayNameToday = t.Accent;
                t.DayDate = Frozen(Rgba(100, 20, 24, 32));
                t.Text = Frozen(Rgba(235, 16, 18, 24));
                t.TextDone = Frozen(Rgba(95, 16, 18, 24));
                t.Muted = Frozen(Rgba(120, 16, 18, 24));
                t.CheckBorder = Frozen(Rgba(110, 16, 18, 24));
                t.CheckHover = Frozen(Rgba(180, 16, 18, 24));
                t.HoverRow = Frozen(Rgba(16, 0, 0, 0));
            }
            return t;
        }
    }
}
