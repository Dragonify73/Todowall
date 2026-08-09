using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TodoWall
{
    /// <summary>
    /// A tiny focusable popup used for all text entry.
    ///
    /// The wall window itself is an Explorer desktop child, so it can take mouse
    /// clicks but cannot reliably own the keyboard focus. Typing therefore happens
    /// in this real top-level window, floated exactly over the row being edited.
    /// </summary>
    internal class QuickEdit : Window
    {
        readonly TextBox _box;
        readonly Grid _grid;
        readonly Rectangle _caret;              // null when animations are off
        readonly TranslateTransform _caretPos;
        bool _caretPlaced;
        bool _closed;
        bool _committed;

        /// <summary>text, and whether the user pressed Enter (i.e. wants to keep going).</summary>
        public Action<string, bool> Committed;
        public Action Cancelled;

        public QuickEdit(Palette theme, double fontSize, string initial, string placeholder)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;

            Color bg = theme.IsDark ? Color.FromArgb(255, 30, 33, 41) : Color.FromArgb(255, 255, 255, 255);

            Border shell = new Border();
            shell.CornerRadius = new CornerRadius(8);
            shell.Background = new SolidColorBrush(bg);
            shell.BorderBrush = theme.Accent;
            shell.BorderThickness = new Thickness(1.5);
            shell.Padding = new Thickness(8, 5, 8, 5);
            shell.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black
            };

            _box = new TextBox();
            _box.Text = initial ?? "";
            _box.FontSize = fontSize;
            _box.Foreground = theme.Text;
            _box.CaretBrush = theme.Accent;
            _box.Background = Brushes.Transparent;
            _box.BorderThickness = new Thickness(0);
            _box.AcceptsReturn = false;
            _box.TextWrapping = TextWrapping.Wrap;
            _box.MaxLength = 400;
            _box.Padding = new Thickness(0);
            _box.VerticalContentAlignment = VerticalAlignment.Center;

            Grid grid = new Grid();
            TextBlock hint = new TextBlock();
            hint.Text = placeholder ?? "";
            hint.FontSize = fontSize;
            hint.Foreground = theme.Muted;
            hint.IsHitTestVisible = false;
            hint.Margin = new Thickness(2, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Visibility = string.IsNullOrEmpty(_box.Text) ? Visibility.Visible : Visibility.Collapsed;
            _box.TextChanged += delegate
            {
                hint.Visibility = string.IsNullOrEmpty(_box.Text) ? Visibility.Visible : Visibility.Collapsed;
            };

            grid.Children.Add(hint);
            grid.Children.Add(_box);
            _grid = grid;

            // WPF's own caret flicks on and off and teleports between columns. With
            // animations on we hide it and drive our own, which fades and glides.
            if (Anim.Enabled)
            {
                _box.CaretBrush = Brushes.Transparent;

                _caretPos = new TranslateTransform();
                _caret = new Rectangle();
                _caret.Width = 1.6;
                _caret.RadiusX = 0.8;
                _caret.RadiusY = 0.8;
                _caret.Height = fontSize * 1.3;
                _caret.Fill = theme.Accent;
                _caret.HorizontalAlignment = HorizontalAlignment.Left;
                _caret.VerticalAlignment = VerticalAlignment.Top;
                _caret.IsHitTestVisible = false;
                _caret.RenderTransform = _caretPos;
                grid.Children.Add(_caret);

                _box.SelectionChanged += delegate { SyncCaret(true); };
                _box.TextChanged += delegate { SyncCaret(true); };
                _box.SizeChanged += delegate { SyncCaret(false); };
            }

            shell.Child = grid;
            Content = shell;

            _box.PreviewKeyDown += OnKey;
            Deactivated += delegate { Commit(false); };
            Loaded += delegate
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                Native.ForceForeground(h);
                Activate();
                _box.Focus();
                _box.SelectAll();
                Keyboard.Focus(_box);
                SyncCaret(true);
            };
        }

        // ===================================================================== caret

        /// <summary>Layout hasn't caught up with the keystroke that triggered this, so
        /// the measurement waits for the render pass.</summary>
        void SyncCaret(bool active)
        {
            if (_caret == null || _closed) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(delegate { PlaceCaret(active); }));
        }

        void PlaceCaret(bool active)
        {
            if (_caret == null || _closed || !_box.IsLoaded) return;

            Rect r;
            try { r = _box.GetRectFromCharacterIndex(_box.CaretIndex); }
            catch { return; }
            if (r.IsEmpty || double.IsInfinity(r.X) || double.IsInfinity(r.Y)) return;

            Point p;
            try { p = _box.TransformToAncestor(_grid).Transform(new Point(r.X, r.Y)); }
            catch { p = new Point(r.X, r.Y); }

            if (r.Height > 1 && Math.Abs(_caret.Height - r.Height) > 0.5) _caret.Height = r.Height;

            // The very first placement snaps: gliding in from the corner would look broken.
            int ms = _caretPlaced ? 110 : 0;
            _caretPlaced = true;
            Glide(TranslateTransform.XProperty, p.X, ms);
            Glide(TranslateTransform.YProperty, p.Y, ms);

            if (active) Blink();
        }

        void Glide(DependencyProperty axis, double to, int ms)
        {
            if (ms <= 0)
            {
                _caretPos.BeginAnimation(axis, null);
                _caretPos.SetValue(axis, to);
                return;
            }
            DoubleAnimation a = new DoubleAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            CubicEase e = new CubicEase();
            e.EasingMode = EasingMode.EaseOut;
            a.EasingFunction = e;
            _caretPos.BeginAnimation(axis, a);
        }

        /// <summary>Restarted on every keystroke: solid while you type, then a slow sine
        /// pulse once you pause.</summary>
        void Blink()
        {
            _caret.BeginAnimation(UIElement.OpacityProperty, null);
            _caret.Opacity = 1;

            SineEase io = new SineEase();
            io.EasingMode = EasingMode.EaseInOut;

            DoubleAnimationUsingKeyFrames k = new DoubleAnimationUsingKeyFrames();
            k.BeginTime = TimeSpan.FromMilliseconds(550);
            k.Duration = new Duration(TimeSpan.FromMilliseconds(1200));
            k.RepeatBehavior = RepeatBehavior.Forever;
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.30), io));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.62), io));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.72), io));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.00), io));

            // This one runs the whole time the popup is open, and the popup is a layered
            // window (software-composited). 30fps is indistinguishable on a slow fade and
            // halves the redraws.
            Timeline.SetDesiredFrameRate(k, 30);
            _caret.BeginAnimation(UIElement.OpacityProperty, k);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit(true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _closed = true;
                if (Cancelled != null) Cancelled();
                Close();
            }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                Commit(true);
            }
        }

        void Commit(bool viaEnter)
        {
            if (_closed || _committed) return;
            _committed = true;
            _closed = true;
            string text = _box.Text == null ? "" : _box.Text.Trim();
            if (Committed != null) Committed(text, viaEnter);
            Close();
        }

        /// <summary>A Forever clock would otherwise stay registered with the timing
        /// manager after the popup is gone.</summary>
        protected override void OnClosed(EventArgs e)
        {
            if (_caret != null)
            {
                _caret.BeginAnimation(UIElement.OpacityProperty, null);
                _caretPos.BeginAnimation(TranslateTransform.XProperty, null);
                _caretPos.BeginAnimation(TranslateTransform.YProperty, null);
            }
            base.OnClosed(e);
        }

        /// <summary>Show over a rectangle given in physical screen pixels.</summary>
        public void ShowAt(double screenX, double screenY, double screenWidth, double dpiScale)
        {
            if (dpiScale <= 0) dpiScale = 1.0;
            Width = Math.Max(120, screenWidth / dpiScale);
            Left = screenX / dpiScale;
            Top = screenY / dpiScale;
            Show();

            // Keep it on screen if the row sits near the bottom edge.
            double vh = SystemParameters.VirtualScreenHeight + SystemParameters.VirtualScreenTop;
            if (Top + ActualHeight > vh) Top = Math.Max(0, vh - ActualHeight - 4);
        }
    }
}
