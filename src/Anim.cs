using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TodoWall
{
    /// <summary>Small, fast motion helpers. Everything here is decorative: when
    /// Anim.Enabled is false each call applies the end state immediately, so behaviour
    /// never depends on an animation having run.</summary>
    internal static class Anim
    {
        public static bool Enabled = true;

        // Motion runs at whatever rate the display offers.
        //
        // These were briefly capped at 30fps to make hovering cheaper, back when hover was
        // a fade. It is instant now, so the cap bought nothing and cost smoothness: a 230ms
        // week slide at 30fps is seven frames, and choppy travel is far more visible than a
        // choppy fade. What is left in here is rare - a week change, a row arriving or
        // collapsing, a checkbox popping - so none of it is worth rationing. The editor
        // caret keeps its own cap, since that one runs for as long as the box is open.

        /// <summary>How long a hover highlight takes to arrive and to leave.
        ///
        /// Zero, deliberately: hover is the one transition a user retriggers several times
        /// a second, and every frame of it is rasterised in software onto a bar-sized
        /// layered surface. Shortening it to 80/100ms halved the frames but read as a snap
        /// anyway - if it is going to look instant it may as well cost one repaint instead
        /// of five. Everything else in this file still animates; a value above zero here
        /// brings the fade back with no other change.</summary>
        public const int HoverIn = 0;
        public const int HoverOut = 0;

        static readonly IEasingFunction OutEase = MakeOut();
        static readonly IEasingFunction InOutEase = MakeInOut();

        static IEasingFunction MakeOut()
        {
            CubicEase e = new CubicEase();
            e.EasingMode = EasingMode.EaseOut;
            e.Freeze();
            return e;
        }

        static IEasingFunction MakeInOut()
        {
            QuadraticEase e = new QuadraticEase();
            e.EasingMode = EasingMode.EaseInOut;
            e.Freeze();
            return e;
        }

        static IEasingFunction Out() { return OutEase; }
        static IEasingFunction InOut() { return InOutEase; }

        /// <summary>Hovering a row starts four of these a second, and a sweep along a
        /// column starts dozens. An animation is a Freezable, so a frozen one can be
        /// handed to BeginAnimation as many times as we like - the callers only ever ask
        /// for a handful of distinct (target, duration) pairs, so they are built once and
        /// kept. Only the undelayed ones are cached: the staggered variants carry a
        /// per-row BeginTime and would grow the table without bound.</summary>
        static readonly Dictionary<long, DoubleAnimation> FadeCache = new Dictionary<long, DoubleAnimation>();
        static readonly Dictionary<long, ColorAnimation> TintCache = new Dictionary<long, ColorAnimation>();

        static DoubleAnimation FadeTo(double to, int ms)
        {
            long key = ((long)ms << 32) ^ (long)(to * 1000);
            DoubleAnimation a;
            if (FadeCache.TryGetValue(key, out a)) return a;

            a = new DoubleAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = OutEase;
            a.Freeze();
            FadeCache[key] = a;
            return a;
        }

        static ColorAnimation TintTo(Color to, int ms)
        {
            long key = ((long)ms << 32) ^ (long)((uint)((to.A << 24) | (to.R << 16) | (to.G << 8) | to.B));
            ColorAnimation a;
            if (TintCache.TryGetValue(key, out a)) return a;

            a = new ColorAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = OutEase;
            a.Freeze();
            TintCache[key] = a;
            return a;
        }

        public static void Fade(UIElement el, double to, int ms, int delayMs)
        {
            if (el == null) return;
            // Clearing first matters: a finished animation goes on holding its end value,
            // and a held animation outranks the local one - see Rewind below.
            if (!Enabled || ms <= 0)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = to;
                return;
            }

            if (delayMs <= 0)
            {
                el.BeginAnimation(UIElement.OpacityProperty, FadeTo(to, ms));
                return;
            }

            DoubleAnimation a = new DoubleAnimation();
            a.To = to;
            a.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = Out();
            a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        public static void Fade(UIElement el, double to, int ms) { Fade(el, to, ms, 0); }

        /// <summary>Animate a colour. The brush must NOT be frozen - callers create a
        /// private SolidColorBrush per element for exactly this reason.</summary>
        public static void Tint(SolidColorBrush brush, Color to, int ms)
        {
            if (brush == null || brush.IsFrozen) return;
            if (!Enabled || ms <= 0)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to;
                return;
            }

            brush.BeginAnimation(SolidColorBrush.ColorProperty, TintTo(to, ms));
        }

        static ScaleTransform EnsureScale(FrameworkElement el)
        {
            ScaleTransform st = el.RenderTransform as ScaleTransform;
            if (st == null)
            {
                st = new ScaleTransform(1, 1);
                el.RenderTransform = st;
                el.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            return st;
        }

        /// <summary>A quick overshoot-and-settle, for confirming a click landed.</summary>
        public static void Pop(FrameworkElement el, double peak, int ms)
        {
            if (el == null || !Enabled) return;
            ScaleTransform st = EnsureScale(el);

            DoubleAnimationUsingKeyFrames k = new DoubleAnimationUsingKeyFrames();
            k.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(peak,
                KeyTime.FromPercent(0.45), Out()));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromPercent(1.0), Out()));

            st.BeginAnimation(ScaleTransform.ScaleXProperty, k);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, k.Clone());
        }

        static TranslateTransform EnsureTranslate(FrameworkElement el)
        {
            TranslateTransform tt = el.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                el.RenderTransform = tt;
            }
            return tt;
        }

        /// <summary>Hand a property back to its local value.
        ///
        /// An animation that has finished keeps *holding* its end value, and a held
        /// animation outranks the local value - so `el.Opacity = 0` before a replay is
        /// silently ignored and the transition simply doesn't play a second time. These
        /// helpers rewind rather than cross-fade, so they must clear first.</summary>
        static void Rewind(UIElement el, TranslateTransform tt, DependencyProperty axis)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            tt.BeginAnimation(axis, null);
        }

        /// <summary>Fade up into place. Used for the bar itself and for newly added rows.</summary>
        public static void RiseIn(FrameworkElement el, double fromY, int ms, int delayMs)
        {
            if (el == null) return;
            if (!Enabled) { el.Opacity = 1; return; }

            TranslateTransform tt = EnsureTranslate(el);
            Rewind(el, tt, TranslateTransform.YProperty);
            tt.Y = fromY;
            el.Opacity = 0;

            DoubleAnimation move = new DoubleAnimation();
            move.To = 0;
            move.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            move.EasingFunction = Out();
            if (delayMs > 0) move.BeginTime = TimeSpan.FromMilliseconds(delayMs);

            tt.BeginAnimation(TranslateTransform.YProperty, move);
            Fade(el, 1, ms, delayMs);
        }

        /// <summary>Slide sideways and fade back in - the week-change transition.</summary>
        public static void SlideIn(FrameworkElement el, double fromX, int ms) { SlideIn(el, fromX, ms, 0); }

        public static void SlideIn(FrameworkElement el, double fromX, int ms, int delayMs)
        {
            if (el == null) return;
            if (!Enabled) { el.Opacity = 1; return; }

            TranslateTransform tt = EnsureTranslate(el);
            Rewind(el, tt, TranslateTransform.XProperty);
            tt.X = fromX;
            el.Opacity = 0;

            DoubleAnimation move = new DoubleAnimation();
            move.To = 0;
            move.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            move.EasingFunction = InOut();
            if (delayMs > 0) move.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            tt.BeginAnimation(TranslateTransform.XProperty, move);
            Fade(el, 1, ms, delayMs);
        }

        /// <summary>Fade out and collapse the height, then run <paramref name="done"/>.
        /// The callback always fires, animations on or off.</summary>
        public static void CollapseOut(FrameworkElement el, int ms, Action done)
        {
            if (el == null) { if (done != null) done(); return; }
            if (!Enabled) { if (done != null) done(); return; }

            double h = el.ActualHeight;
            if (h <= 0) { if (done != null) done(); return; }

            el.IsHitTestVisible = false;

            DoubleAnimation shrink = new DoubleAnimation();
            shrink.From = h;
            shrink.To = 0;
            shrink.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            shrink.EasingFunction = Out();

            bool finished = false;
            EventHandler onDone = null;
            onDone = delegate
            {
                if (finished) return;
                finished = true;
                shrink.Completed -= onDone;
                if (done != null) done();
            };
            shrink.Completed += onDone;

            Fade(el, 0, Math.Max(60, ms - 40));
            el.BeginAnimation(FrameworkElement.HeightProperty, shrink);
        }
    }
}
