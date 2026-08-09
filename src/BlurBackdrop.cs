using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TodoWall
{
    /// <summary>
    /// Builds the frosted-glass panel background: the slice of wallpaper that sits behind
    /// the bar, blurred.
    ///
    /// It is rendered from the wallpaper FILE rather than grabbed off the screen. A screen
    /// capture would bake in desktop icons and whatever window happened to be sitting
    /// there, and would need the bar hidden at exactly the right moment. Reproducing
    /// Windows' own fit maths against the source image avoids all of that.
    ///
    /// Everything is sized for the OUTPUT, not the source: the wallpaper is decoded
    /// directly at roughly the blur resolution (a couple of hundred pixels wide), so a
    /// 4K wallpaper costs no more than a small one. Decoding at full size first would
    /// burn ~9MB per megapixel to produce a thumbnail.
    /// </summary>
    internal static class BlurBackdrop
    {
        const int Down = 6;     // blur at 1/6 scale, then let WPF smooth it back up
        const int Passes = 3;   // three box passes approximate a gaussian

        /// <summary>The wallpaper Windows is currently showing. Read live rather than
        /// stored, so the backdrop follows whatever you set in Windows Settings.</summary>
        public static string ResolveWallpaper()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                {
                    if (k != null)
                    {
                        string p = k.GetValue("WallPaper") as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>How Windows is fitting that wallpaper, as one of the names DestRect
        /// understands. Same registry values the Personalisation page writes.</summary>
        public static string ResolveStyle()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                {
                    if (k == null) return "Fill";
                    if ((k.GetValue("TileWallpaper") as string) == "1") return "Tile";
                    switch (k.GetValue("WallpaperStyle") as string)
                    {
                        case "0": return "Center";
                        case "2": return "Stretch";
                        case "6": return "Fit";
                        case "22": return "Span";
                        default: return "Fill";   // "10"
                    }
                }
            }
            catch { return "Fill"; }
        }

        /// <summary>Identifies the current wallpaper for change detection, so the backdrop
        /// is only re-rendered when the image or its fit actually changed.</summary>
        public static string SourceKey()
        {
            string path = ResolveWallpaper();
            if (path == null) return "none";
            string stamp = "";
            try { stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString(); }
            catch { }
            return path + "|" + ResolveStyle() + "|" + stamp;
        }

        /// <param name="tint">Colour blended into the blur to keep text readable.</param>
        /// <returns>A frozen brush, or null if there is nothing usable to blur.</returns>
        public static ImageBrush Create(
            int barX, int barY, int barW, int barH,
            System.Drawing.Rectangle screen, double radiusPx, Color tint, double tintAmount)
        {
            if (barW <= 0 || barH <= 0) return null;
            string path = ResolveWallpaper();
            if (path == null) return null;
            string style = ResolveStyle();

            try
            {
                int sourceW, sourceH;
                if (!ReadSize(path, out sourceW, out sourceH)) return null;

                int sw = Math.Max(2, barW / Down);
                int sh = Math.Max(2, barH / Down);

                bool tile = string.Equals(style, "Tile", StringComparison.OrdinalIgnoreCase);
                Rect dest = tile
                    ? new Rect(screen.X, screen.Y, sourceW, sourceH)
                    : DestRect(style, sourceW, sourceH, screen);

                // Decode only as many pixels as survive the downscale.
                int decodeW = (int)Math.Ceiling(dest.Width / Down) + 2;
                if (decodeW < 8) decodeW = 8;
                if (decodeW > sourceW) decodeW = sourceW;

                BitmapSource image = Decode(path, decodeW);
                if (image == null) return null;

                Rect local = new Rect(
                    (dest.X - barX) / Down, (dest.Y - barY) / Down,
                    dest.Width / Down, dest.Height / Down);

                DrawingVisual visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, sw, sh)); // matches "Fit" letterboxing
                    if (tile)
                    {
                        ImageBrush tiled = new ImageBrush(image);
                        tiled.TileMode = TileMode.Tile;
                        tiled.ViewportUnits = BrushMappingMode.Absolute;
                        tiled.Viewport = local;
                        tiled.Stretch = Stretch.Fill;
                        dc.DrawRectangle(tiled, null, new Rect(0, 0, sw, sh));
                    }
                    else
                    {
                        dc.DrawImage(image, local);
                    }
                }

                RenderTargetBitmap rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                int stride = sw * 4;
                byte[] buf = new byte[stride * sh];
                rtb.CopyPixels(buf, stride, 0);

                int radius = (int)Math.Max(1, Math.Round(radiusPx / Down));
                BoxBlur(buf, sw, sh, stride, radius, tint, tintAmount);

                WriteableBitmap output = new WriteableBitmap(sw, sh, 96, 96, PixelFormats.Bgra32, null);
                output.WritePixels(new Int32Rect(0, 0, sw, sh), buf, stride, 0);
                output.Freeze();

                ImageBrush brush = new ImageBrush(output);
                brush.Stretch = Stretch.Fill;
                brush.Freeze();
                return brush;
            }
            catch (Exception ex)
            {
                Log.Write("backdrop failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Read just the header - no pixels are decoded.</summary>
        static bool ReadSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(fs,
                        BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0) return false;
                    width = decoder.Frames[0].PixelWidth;
                    height = decoder.Frames[0].PixelHeight;
                    return width > 0 && height > 0;
                }
            }
            catch { return false; }
        }

        static BitmapSource Decode(string path, int decodeWidth)
        {
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.CacheOption = BitmapCacheOption.OnLoad;   // decode now, then release the file
                bi.DecodePixelWidth = decodeWidth;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        /// <summary>Where Windows would paint the wallpaper, in screen coordinates.</summary>
        static Rect DestRect(string style, int iw, int ih, System.Drawing.Rectangle screen)
        {
            string s = (style ?? "Fill").ToLowerInvariant();
            Rect full = new Rect(screen.X, screen.Y, screen.Width, screen.Height);
            if (iw <= 0 || ih <= 0) return full;
            if (s == "stretch") return full;

            if (s == "center")
                return new Rect(screen.X + (screen.Width - iw) / 2.0,
                                screen.Y + (screen.Height - ih) / 2.0, iw, ih);

            double scale = (s == "fit")
                ? Math.Min(screen.Width / (double)iw, screen.Height / (double)ih)
                : Math.Max(screen.Width / (double)iw, screen.Height / (double)ih); // fill, span

            double w = iw * scale, h = ih * scale;
            return new Rect(screen.X + (screen.Width - w) / 2.0,
                            screen.Y + (screen.Height - h) / 2.0, w, h);
        }

        // ---------------------------------------------------------------- blur

        static void BoxBlur(byte[] buf, int w, int h, int stride, int radius, Color tint, double tintAmount)
        {
            byte[] tmp = new byte[buf.Length];
            for (int p = 0; p < Passes; p++)
            {
                BlurHorizontal(buf, tmp, w, h, stride, radius);
                BlurVertical(tmp, buf, w, h, stride, radius);
            }

            if (tintAmount > 0)
            {
                double a = Math.Min(1.0, tintAmount);
                double keep = 1 - a;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        buf[i] = (byte)(buf[i] * keep + tint.B * a);
                        buf[i + 1] = (byte)(buf[i + 1] * keep + tint.G * a);
                        buf[i + 2] = (byte)(buf[i + 2] * keep + tint.R * a);
                        buf[i + 3] = 255;
                    }
                }
            }
        }

        static void BlurHorizontal(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (int x = 0; x <= r && x < w; x++) { sum += src[row + x * 4 + c]; count++; }
                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / count);
                        int add = x + r + 1, drop = x - r;
                        if (add < w) { sum += src[row + add * 4 + c]; count++; }
                        if (drop >= 0) { sum -= src[row + drop * 4 + c]; count--; }
                    }
                }
            }
        }

        static void BlurVertical(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (int y = 0; y <= r && y < h; y++) { sum += src[y * stride + col + c]; count++; }
                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col + c] = (byte)(sum / count);
                        int add = y + r + 1, drop = y - r;
                        if (add < h) { sum += src[add * stride + col + c]; count++; }
                        if (drop >= 0) { sum -= src[drop * stride + col + c]; count--; }
                    }
                }
            }
        }
    }
}
