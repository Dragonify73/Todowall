using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TodoWall
{
    /// <summary>One task. Owns its own animated brushes so ticking a task can transition
    /// in place instead of forcing the whole column to be rebuilt.</summary>
    internal class TaskRow : Border
    {
        public readonly TodoItem Item;
        public readonly int WeekIndex;
        public readonly int Day;
        /// <summary>The calendar date of the cell this row sits in - the anchor the
        /// "Move to" targets are resolved against.</summary>
        public readonly DateTime Date;
        public int Index;

        public Action<TaskRow> Toggled;
        public Action<TaskRow> EditRequested;
        public Action<TaskRow> DeleteRequested;
        public Action<TaskRow, DateTime> MoveRequested;

        readonly Palette _p;
        readonly double _fs;

        readonly SolidColorBrush _rowFill;
        readonly SolidColorBrush _textFill;
        readonly SolidColorBrush _boxFill;
        readonly SolidColorBrush _boxStroke;

        readonly Border _check;
        readonly System.Windows.Shapes.Path _tick;
        readonly TextBlock _text;
        readonly Border _delete;

        /// <summary>The checkmark and the delete cross, parsed once for the whole app.
        /// Both are Stretch.Uniform, so the same geometry serves every row at every font
        /// size - and a frozen Geometry can be shared by any number of Paths. Parsing them
        /// per row meant re-running the path mini-language twice for every task on every
        /// rebuild, which is the whole grid each time the week changes.</summary>
        static readonly Geometry TickGeometry = Frozen("M 0,4.2 L 3.3,7.5 L 9,1.2");
        static readonly Geometry CrossGeometry = Frozen("M 0,0 L 8,8 M 8,0 L 0,8");

        static Geometry Frozen(string path)
        {
            Geometry g = Geometry.Parse(path);
            g.Freeze();
            return g;
        }

        static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Transparent;
        }

        public TaskRow(Palette palette, double fontSize, TodoItem item, int weekIndex, int day, int index, DateTime date)
        {
            _p = palette;
            _fs = fontSize;
            Item = item;
            WeekIndex = weekIndex;
            Day = day;
            Date = date.Date;
            Index = index;

            _rowFill = new SolidColorBrush(Colors.Transparent);
            _textFill = new SolidColorBrush(ColorOf(item.Done ? _p.TextDone : _p.Text));
            _boxFill = new SolidColorBrush(item.Done ? ColorOf(_p.Accent) : Colors.Transparent);
            _boxStroke = new SolidColorBrush(ColorOf(item.Done ? _p.Accent : _p.CheckBorder));

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ---- checkbox
            double box = Math.Round(_fs * 1.2);
            _check = new Border();
            _check.Width = box;
            _check.Height = box;
            _check.CornerRadius = new CornerRadius(box / 2);
            _check.BorderThickness = new Thickness(1.5);
            _check.BorderBrush = _boxStroke;
            _check.Background = _boxFill;
            _check.VerticalAlignment = VerticalAlignment.Top;
            _check.Margin = new Thickness(0, _fs * 0.16, _fs * 0.45, 0);
            _check.Cursor = Cursors.Hand;
            _check.RenderTransformOrigin = new Point(0.5, 0.5);
            _check.RenderTransform = new ScaleTransform(1, 1);

            _tick = new System.Windows.Shapes.Path();
            _tick.Data = TickGeometry;
            _tick.Stroke = _p.IsDark ? Brushes.Black : Brushes.White;
            _tick.StrokeThickness = 1.9;
            _tick.StrokeStartLineCap = PenLineCap.Round;
            _tick.StrokeEndLineCap = PenLineCap.Round;
            _tick.StrokeLineJoin = PenLineJoin.Round;
            _tick.Stretch = Stretch.Uniform;
            _tick.Margin = new Thickness(box * 0.24);
            _tick.Opacity = item.Done ? 1 : 0;
            _check.Child = _tick;

            Grid.SetColumn(_check, 0);
            g.Children.Add(_check);

            // ---- text
            _text = new TextBlock();
            _text.Text = item.Text;
            _text.FontSize = _fs;
            _text.TextWrapping = TextWrapping.Wrap;
            _text.Foreground = _textFill;
            _text.Cursor = Cursors.IBeam;
            _text.VerticalAlignment = VerticalAlignment.Center;
            if (item.Done) _text.TextDecorations = TextDecorations.Strikethrough;
            Grid.SetColumn(_text, 1);
            g.Children.Add(_text);

            // ---- delete
            System.Windows.Shapes.Path cross = new System.Windows.Shapes.Path();
            cross.Data = CrossGeometry;
            cross.Stroke = _p.Muted;
            cross.StrokeThickness = 1.4;
            cross.StrokeStartLineCap = PenLineCap.Round;
            cross.StrokeEndLineCap = PenLineCap.Round;
            cross.Width = _fs * 0.66;
            cross.Height = _fs * 0.66;
            cross.Stretch = Stretch.Uniform;

            _delete = new Border();
            _delete.Child = cross;
            _delete.Padding = new Thickness(_fs * 0.28);
            _delete.CornerRadius = new CornerRadius(4);
            _delete.Background = Brushes.Transparent;
            _delete.Cursor = Cursors.Hand;
            _delete.Opacity = 0;
            _delete.VerticalAlignment = VerticalAlignment.Top;
            _delete.ToolTip = "Delete task";
            Grid.SetColumn(_delete, 2);
            g.Children.Add(_delete);

            // ---- row
            Child = g;
            Background = _rowFill;
            Padding = new Thickness(_fs * 0.32, _fs * 0.26, _fs * 0.16, _fs * 0.26);
            CornerRadius = new CornerRadius(6);
            Margin = new Thickness(0, 0, 0, _fs * 0.1);

            // Hover is the one transition that runs constantly, and every frame of it is
            // rasterised in software onto a bar-sized layered surface - measured at 37.8ms
            // of CPU per second while sweeping a column, against 0 when still. The frame
            // count is duration x framerate, so the shorter pair below is most of that cost
            // gone while still reading as a fade rather than a snap.
            MouseEnter += delegate
            {
                Anim.Tint(_rowFill, ColorOf(_p.HoverRow), Anim.HoverIn);
                Anim.Fade(_delete, 1, Anim.HoverIn);
            };
            MouseLeave += delegate
            {
                Anim.Tint(_rowFill, Colors.Transparent, Anim.HoverOut);
                Anim.Fade(_delete, 0, Anim.HoverOut);
            };

            _check.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (Toggled != null) Toggled(this);
            };
            _text.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (EditRequested != null) EditRequested(this);
            };
            _delete.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (DeleteRequested != null) DeleteRequested(this);
            };
            MouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    if (DeleteRequested != null) DeleteRequested(this);
                }
            };

            // The ✕ sits inside the row, so entering it never left the row: re-running the
            // hover tint here only restarted a clock that had already settled on its
            // target colour.

            // A menu costs more to build than the rest of the row put together, and every
            // refresh throws all the rows away and makes new ones - so most menus built
            // eagerly were never shown once. Building it on the way down to the right-click
            // still has it in place by the time WPF looks for it on the way back up.
            PreviewMouseRightButtonDown += delegate
            {
                if (ContextMenu == null) ContextMenu = BuildMenu();
            };
        }

        ContextMenu BuildMenu()
        {
            ContextMenu m = new ContextMenu();

            MenuItem edit = new MenuItem();
            edit.Header = "Edit…";
            edit.Icon = MenuChrome.GlyphEdit;
            edit.Click += delegate { if (EditRequested != null) EditRequested(this); };
            m.Items.Add(edit);

            MenuItem toggle = new MenuItem();
            toggle.Header = Item.Done ? "Mark as not done" : "Mark as done";
            toggle.Icon = Item.Done ? MenuChrome.GlyphUndone : MenuChrome.GlyphDone;
            toggle.Click += delegate { if (Toggled != null) Toggled(this); };
            m.Items.Add(toggle);
            m.Opened += delegate
            {
                toggle.Header = Item.Done ? "Mark as not done" : "Mark as done";
                toggle.Icon = Item.Done ? MenuChrome.GlyphUndone : MenuChrome.GlyphDone;
            };

            MenuItem move = new MenuItem();
            move.Header = "Move to";
            move.Icon = MenuChrome.GlyphMove;
            BuildMoveTargets(move);
            // Rebuilt on open so the window sitting through midnight can't offer stale dates.
            m.Opened += delegate { BuildMoveTargets(move); };
            m.Items.Add(move);

            m.Items.Add(new Separator());

            MenuItem del = new MenuItem();
            del.Header = "Delete";
            del.Icon = MenuChrome.GlyphDelete;
            del.Click += delegate { if (DeleteRequested != null) DeleteRequested(this); };
            m.Items.Add(del);

            return m;
        }

        static readonly string[] DayNames =
            { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        /// <summary>A weekday only ever means the *next* one: picking a day that has already
        /// gone by lands on the coming occurrence rather than a date in the past.</summary>
        DateTime TargetFor(int weekday)
        {
            DateTime t = Date.AddDays(weekday - Day);   // same week as this row
            DateTime today = DateTime.Today;
            if (t < today)
            {
                int weeks = ((today - t).Days + 6) / 7;
                t = t.AddDays(7 * weeks);
            }
            return t;
        }

        void BuildMoveTargets(MenuItem move)
        {
            move.Items.Clear();
            for (int d = 0; d < 7; d++)
            {
                DateTime target = TargetFor(d);
                if (target == Date) continue;           // that's where it already is

                MenuItem mi = new MenuItem();
                // Spell the date out whenever the pick rolls into another week, so
                // "Monday" can't be read as the Monday that has already passed.
                mi.Header = Board.MondayOf(target) == Board.MondayOf(Date)
                    ? DayNames[d]
                    : DayNames[d] + "  ·  " + target.ToString("d MMM", CultureInfo.CurrentCulture);
                mi.Click += delegate { if (MoveRequested != null) MoveRequested(this, target); };
                move.Items.Add(mi);
            }
        }

        /// <summary>Transition the row to match Item.Done.</summary>
        public void ApplyDoneState(bool animate)
        {
            int ms = animate ? 180 : 0;

            Anim.Tint(_textFill, ColorOf(Item.Done ? _p.TextDone : _p.Text), ms);
            Anim.Tint(_boxFill, Item.Done ? ColorOf(_p.Accent) : Colors.Transparent, ms);
            Anim.Tint(_boxStroke, ColorOf(Item.Done ? _p.Accent : _p.CheckBorder), ms);
            Anim.Fade(_tick, Item.Done ? 1 : 0, animate ? 140 : 0);

            // Strikethrough can't be animated, so it lands with the colour change.
            _text.TextDecorations = Item.Done ? TextDecorations.Strikethrough : null;

            if (animate) Anim.Pop(_check, Item.Done ? 1.22 : 0.88, 240);
        }

        public void SetText(string text)
        {
            _text.Text = text;
        }

        public void PlayEnter(int delayMs)
        {
            Anim.RiseIn(this, 8, 220, delayMs);
        }
    }
}
