using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace TodoWall
{
    /// <summary>
    /// Gives every context menu in the app the Windows 11 flyout look: an 8px rounded
    /// card, a hairline border, a soft shadow, frosted glass behind it, and rows that
    /// highlight in the accent colour from Settings.
    ///
    /// The chrome is a restyle rather than a hand-built popup on purpose - WPF's own
    /// menu machinery (mouse capture, dismissal, keyboard walking, submenu timing) is
    /// the part that is hard to get right under a window that never takes focus, and
    /// none of it is visual. Only the templates change.
    ///
    /// The glass is the same wallpaper blur the bar itself uses rather than a DWM
    /// acrylic: a WPF menu popup is a per-pixel-alpha layered window, which the
    /// composition-attribute blur does not apply to, and blurring the wallpaper gives
    /// the menu exactly the bar's material anyway - which is what it opens on top of.
    /// </summary>
    internal static class MenuChrome
    {
        // Gutter left inside the popup for the shadow to spill into. The popup window is
        // sized to the card PLUS this, so it is also what FrostCard() has to subtract off
        // to find where the card actually sits on screen - hence one definition, injected
        // into the templates rather than written twice.
        const int GutterL = 8, GutterT = 6, GutterR = 8, GutterB = 12;
        const int Radius = 8;

        static bool _installed;
        static ResourceDictionary _dict;

        /// <summary>Hook the app up once, at startup.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            // Class handlers rather than per-menu wiring: this way the menus WPF builds
            // for itself (the cut/copy/paste one on the task editor) are dressed too.
            EventManager.RegisterClassHandler(typeof(ContextMenu),
                ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuOpened));
            EventManager.RegisterClassHandler(typeof(MenuItem),
                MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));

            Refresh();
        }

        /// <summary>Rebuild the styles against the current skin. Call after the palette
        /// changes; menus pick the new dictionary up without being rebuilt.</summary>
        public static void Refresh()
        {
            Application app = Application.Current;
            if (app == null) return;
            try
            {
                ResourceDictionary built = Build(Core.Skin);
                if (_dict != null) app.Resources.MergedDictionaries.Remove(_dict);
                _dict = built;
                app.Resources.MergedDictionaries.Add(_dict);
            }
            catch (Exception ex) { Program.LogCrash(ex); }
        }

        // ================================================================== the glass

        static void OnMenuOpened(object sender, RoutedEventArgs e)
        {
            ContextMenu m = sender as ContextMenu;
            if (m == null) return;
            Dress(Part(m, "PART_Chrome"), Part(m, "PART_Tint"));
        }

        static void OnSubmenuOpened(object sender, RoutedEventArgs e)
        {
            // The event bubbles, so every ancestor item sees a grandchild's submenu open.
            if (!ReferenceEquals(sender, e.OriginalSource)) return;
            MenuItem mi = sender as MenuItem;
            if (mi == null) return;
            Dress(Part(mi, "PART_SubChrome"), Part(mi, "PART_SubTint"));
        }

        static void Dress(Border chrome, Border tint)
        {
            if (chrome == null) return;
            PlayOpen(chrome);
            // Nothing is placed or measured yet at Opened; the blur needs both.
            chrome.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(delegate
                {
                    FrostCard(chrome, tint, new Thickness(GutterL, GutterT, GutterR, GutterB));
                }));
        }

        /// <summary>Cut the slice of blurred wallpaper sitting behind this card and paint
        /// it with it. Falls back to the flat panel colour the template already carries, so
        /// a failure here is invisible rather than fatal.
        ///
        /// Shared rather than menu-private: the Settings panel is made of the same card
        /// material, and a second copy of this would be a second thing to keep in step with
        /// the palette. <paramref name="gutter"/> is the margin the card sits inside its own
        /// window by - the room left for its shadow - which is what has to come back off the
        /// window rect to find where the card actually lands on screen.</summary>
        public static void FrostCard(Border chrome, Border tint, Thickness gutter)
        {
            if (chrome == null || !chrome.IsVisible) return;

            Palette p = Core.Skin;
            Color panel = ColorOf(p.Panel);
            ImageBrush glass = Core.Config.Blur ? Cut(chrome, panel, gutter) : null;

            if (glass == null)
            {
                chrome.Background = Flat(panel, FlatAlpha);
                if (tint != null) tint.Background = Brushes.Transparent;
                return;
            }

            chrome.Background = glass;
            // The blur is opaque, so this tint on top is what makes the text readable.
            if (tint != null) tint.Background = Flat(panel, GlassAlpha);
        }

        /// <summary>Menus are read at a glance and sit over arbitrary desktop clutter, so
        /// they are kept more solid than the bar - but still track the Settings slider,
        /// because a near-transparent bar with a slab of a menu on it looks like two
        /// different programs. Unblurred needs the most help: there is nothing behind the
        /// tint but raw wallpaper.</summary>
        public static double GlassAlpha { get { return Clamp(Core.Config.Opacity, 0.70, 0.92); } }
        public static double FlatAlpha { get { return Clamp(Core.Config.Opacity + 0.30, 0.80, 0.97); } }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static ImageBrush Cut(Border chrome, Color panel, Thickness gutter)
        {
            try
            {
                HwndSource src = PresentationSource.FromVisual(chrome) as HwndSource;
                if (src == null || src.CompositionTarget == null) return null;

                // The popup's own window rect, not chrome.PointToScreen: the open
                // animation is a render transform, and PointToScreen reports through it.
                Matrix m = src.CompositionTarget.TransformToDevice;
                double sx = m.M11 > 0 ? m.M11 : 1, sy = m.M22 > 0 ? m.M22 : 1;

                Native.RECT r;
                if (!Native.GetWindowRect(src.Handle, out r)) return null;

                int x = r.Left + (int)Math.Round(gutter.Left * sx);
                int y = r.Top + (int)Math.Round(gutter.Top * sy);
                int w = (r.Right - r.Left) - (int)Math.Round((gutter.Left + gutter.Right) * sx);
                int h = (r.Bottom - r.Top) - (int)Math.Round((gutter.Top + gutter.Bottom) * sy);
                if (w < 4 || h < 4) return null;

                System.Drawing.Rectangle screen =
                    WinForms.Screen.FromPoint(new System.Drawing.Point(x + w / 2, y + h / 2)).Bounds;

                return BlurBackdrop.Create(x, y, w, h, screen,
                    Core.Config.BlurStrength * sy,
                    Color.FromRgb(panel.R, panel.G, panel.B), 0.12);
            }
            catch (Exception ex)
            {
                Log.Write("menu backdrop failed: " + ex.Message);
                return null;
            }
        }

        // ================================================================== motion

        static readonly IEasingFunction Ease = MakeEase();

        static IEasingFunction MakeEase()
        {
            CubicEase e = new CubicEase();
            e.EasingMode = EasingMode.EaseOut;
            e.Freeze();
            return e;
        }

        /// <summary>Windows 11's flyouts unfold from the corner they were opened at.</summary>
        static void PlayOpen(Border chrome)
        {
            if (!Anim.Enabled)
            {
                chrome.BeginAnimation(UIElement.OpacityProperty, null);
                chrome.Opacity = 1;
                return;
            }

            ScaleTransform st = chrome.RenderTransform as ScaleTransform;
            if (st == null)
            {
                st = new ScaleTransform(1, 1);
                chrome.RenderTransform = st;
            }
            chrome.RenderTransformOrigin = new Point(0, 0);

            DoubleAnimation grow = new DoubleAnimation();
            grow.From = 0.88;
            grow.To = 1;
            grow.Duration = new Duration(TimeSpan.FromMilliseconds(150));
            grow.EasingFunction = Ease;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

            chrome.BeginAnimation(UIElement.OpacityProperty, null);
            chrome.Opacity = 0;
            Anim.Fade(chrome, 1, 110);
        }

        // ================================================================== plumbing

        static Border Part(Control owner, string name)
        {
            if (owner == null || owner.Template == null) return null;
            owner.ApplyTemplate();
            return owner.Template.FindName(name, owner) as Border;
        }

        static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Transparent;
        }

        static Brush Flat(Color c, double alpha)
        {
            SolidColorBrush b = new SolidColorBrush(
                Color.FromArgb((byte)Math.Round(Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        static string Hex(Color c) { return Hex(c, c.A / 255.0); }

        static string Hex(Color c, double alpha)
        {
            byte a = (byte)Math.Round(Clamp(alpha, 0, 1) * 255);
            return "#" + a.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static string Num(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // ================================================================== styles

        static ResourceDictionary Build(Palette p)
        {
            Color panel = ColorOf(p.Panel);
            Color accent = p.AccentColor;

            // Menu text is set from the Win11 metric (14px) rather than the bar's font
            // size, but follows it far enough that a large bar doesn't get a tiny menu.
            double fs = Clamp(Core.Config.FontSize * 0.95, 12.5, 16);

            string xaml = Xaml
                .Replace("@gutter@", GutterL + "," + GutterT + "," + GutterR + "," + GutterB)
                .Replace("@radius@", Radius.ToString(CultureInfo.InvariantCulture))
                .Replace("@subx@", (2 - GutterL).ToString(CultureInfo.InvariantCulture))
                .Replace("@suby@", (-5 - GutterT).ToString(CultureInfo.InvariantCulture))
                .Replace("@fontsize@", Num(fs))
                .Replace("@itemh@", Num(Math.Round(Clamp(fs * 2.2, 28, 42))))
                .Replace("@fill@", Hex(panel, FlatAlpha))
                .Replace("@text@", Hex(ColorOf(p.Text)))
                .Replace("@muted@", Hex(ColorOf(p.Muted)))
                .Replace("@divider@", Hex(ColorOf(p.Divider)))
                .Replace("@border@", Hex(p.IsDark ? Colors.White : Colors.Black, p.IsDark ? 0.10 : 0.09))
                .Replace("@accent@", Hex(accent, 1))
                .Replace("@hover@", Hex(accent, p.IsDark ? 0.19 : 0.16))
                .Replace("@pressed@", Hex(accent, p.IsDark ? 0.30 : 0.26));

            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        // Written as markup because a control template is markup: the code equivalent is
        // FrameworkElementFactory, which says the same thing three times as long. Colours
        // and metrics are substituted above so the palette stays the single source.
        //
        // Single quotes throughout - it saves doubling every quote in the C# literal.
        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <Style TargetType='{x:Type ContextMenu}'>
    <Setter Property='Foreground' Value='@text@'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='@fontsize@'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ContextMenu}'>
          <Border x:Name='PART_Chrome' Margin='@gutter@' CornerRadius='@radius@'
                  Background='@fill@' BorderBrush='@border@' BorderThickness='1'
                  SnapsToDevicePixels='True'
                  RenderOptions.BitmapScalingMode='LowQuality'>
            <Border.Effect>
              <DropShadowEffect Color='#FF000000' BlurRadius='14' ShadowDepth='4'
                                Direction='270' Opacity='0.36'/>
            </Border.Effect>
            <Grid>
              <Border x:Name='PART_Tint' CornerRadius='@radius@' Background='#00000000'/>
              <ItemsPresenter Margin='4' KeyboardNavigation.DirectionalNavigation='Cycle'/>
            </Grid>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='{x:Type Separator}'>
    <Setter Property='Height' Value='1'/>
    <Setter Property='Margin' Value='11,4,11,4'/>
    <Setter Property='Background' Value='@divider@'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Separator}'>
          <Border Height='1' Background='{TemplateBinding Background}' SnapsToDevicePixels='True'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type MenuItem}'>
    <Setter Property='Foreground' Value='@text@'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type MenuItem}'>
          <Grid Background='#00FFFFFF'>
            <Border x:Name='Fill' CornerRadius='4' Background='#00FFFFFF'/>
            <Grid MinHeight='@itemh@' Margin='11,0,11,0'>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='Auto'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>

              <TextBlock x:Name='Glyph' Grid.Column='0' Width='16' Margin='0,0,12,0'
                         VerticalAlignment='Center' TextAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='15'
                         Foreground='@muted@'
                         Text='{Binding Icon, RelativeSource={RelativeSource TemplatedParent}}'/>

              <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True'
                                VerticalAlignment='Center' Margin='0,5,0,6'/>

              <TextBlock x:Name='Gesture' Grid.Column='2' Margin='24,0,0,0' Opacity='0.75'
                         VerticalAlignment='Center' Foreground='@muted@'
                         Text='{Binding InputGestureText, RelativeSource={RelativeSource TemplatedParent}}'/>

              <TextBlock x:Name='Chevron' Grid.Column='3' Text='&#xE76C;' Visibility='Collapsed'
                         Margin='18,0,0,0' VerticalAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='11'
                         Foreground='@muted@'/>
            </Grid>

            <Popup x:Name='PART_Popup' AllowsTransparency='True' Focusable='False'
                   Placement='Right' HorizontalOffset='@subx@' VerticalOffset='@suby@'
                   PlacementTarget='{Binding RelativeSource={RelativeSource TemplatedParent}}'
                   IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border x:Name='PART_SubChrome' Margin='@gutter@' CornerRadius='@radius@'
                      Background='@fill@' BorderBrush='@border@' BorderThickness='1'
                      SnapsToDevicePixels='True'
                      RenderOptions.BitmapScalingMode='LowQuality'>
                <Border.Effect>
                  <DropShadowEffect Color='#FF000000' BlurRadius='14' ShadowDepth='4'
                                    Direction='270' Opacity='0.36'/>
                </Border.Effect>
                <Grid>
                  <Border x:Name='PART_SubTint' CornerRadius='@radius@' Background='#00000000'/>
                  <StackPanel IsItemsHost='True' Margin='4'
                              KeyboardNavigation.DirectionalNavigation='Cycle'/>
                </Grid>
              </Border>
            </Popup>
          </Grid>

          <ControlTemplate.Triggers>
            <Trigger Property='HasItems' Value='True'>
              <Setter TargetName='Chevron' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='Icon' Value='{x:Null}'>
              <Setter TargetName='Glyph' Property='Visibility' Value='Collapsed'/>
            </Trigger>
            <Trigger Property='InputGestureText' Value=''>
              <Setter TargetName='Gesture' Property='Visibility' Value='Collapsed'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Fill' Property='Background' Value='@hover@'/>
              <Setter TargetName='Glyph' Property='Foreground' Value='@accent@'/>
              <Setter TargetName='Chevron' Property='Foreground' Value='@text@'/>
            </Trigger>
            <Trigger Property='IsSubmenuOpen' Value='True'>
              <Setter TargetName='Fill' Property='Background' Value='@hover@'/>
              <Setter TargetName='Glyph' Property='Foreground' Value='@accent@'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Fill' Property='Background' Value='@pressed@'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.4'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>";

        // Segoe Fluent Icons codepoints - the same set Windows dresses its own menus
        // with. Written escaped so the source file stays plain ASCII. An item that
        // leaves Icon null drops the glyph column rather than sitting indented
        // against nothing, so passing null is a fine answer too.
        public const string GlyphEdit = "\uE70F";       // pencil
        public const string GlyphDone = "\uE73E";       // checkmark
        public const string GlyphUndone = "\uE739";     // empty checkbox
        public const string GlyphMove = "\uE787";       // calendar
        public const string GlyphDelete = "\uE74D";     // bin
        public const string GlyphSettings = "\uE713";   // gear
        public const string GlyphClear = "\uE894";      // clear
        public const string GlyphAttach = "\uE72C";     // refresh
        public const string GlyphExit = "\uE7E8";       // power
        public const string GlyphColor = "\uE790";      // palette
        public const string GlyphClock = "\uE917";      // clock face
        public const string GlyphPerson = "\uE77B";     // contact
        public const string GlyphClose = "\uE8BB";      // window close
    }
}
