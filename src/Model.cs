using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TodoWall
{
    internal class TodoItem
    {
        public string Text = "";
        public bool Done;
        public DateTime? DoneAt;
    }

    /// <summary>A single Monday-anchored week: seven ordered task lists, Mon..Sun.</summary>
    internal class Week
    {
        public readonly List<TodoItem>[] Days = new List<TodoItem>[7];
        public Week()
        {
            for (int i = 0; i < 7; i++) Days[i] = new List<TodoItem>();
        }
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < 7; i++) if (Days[i].Count > 0) return false;
                return true;
            }
        }
    }

    internal class Board
    {
        // key = Monday of the week, "yyyy-MM-dd"
        public readonly Dictionary<string, Week> Weeks = new Dictionary<string, Week>(StringComparer.Ordinal);

        public static DateTime MondayOf(DateTime d)
        {
            int delta = ((int)d.DayOfWeek + 6) % 7; // Mon=0 .. Sun=6
            return d.Date.AddDays(-delta);
        }

        public static string Key(DateTime monday)
        {
            return monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public Week Get(DateTime monday, bool create)
        {
            string k = Key(monday);
            Week w;
            if (Weeks.TryGetValue(k, out w)) return w;
            if (!create) return null;
            w = new Week();
            Weeks[k] = w;
            return w;
        }

        public bool Has(DateTime monday)
        {
            return Weeks.ContainsKey(Key(monday));
        }

        /// <summary>Materialise the current week, applying the rollover policy the first
        /// time a new week is opened.</summary>
        public Week EnsureCurrentWeek(DateTime monday, string rollover)
        {
            if (Has(monday)) return Get(monday, true);

            Week fresh = Get(monday, true);
            Week prev = Get(monday.AddDays(-7), false);
            if (prev == null || rollover == "ClearAll") return fresh;

            if (rollover == "KeepAll")
            {
                for (int i = 0; i < 7; i++)
                    foreach (TodoItem t in prev.Days[i])
                        fresh.Days[i].Add(new TodoItem { Text = t.Text, Done = t.Done, DoneAt = t.DoneAt });
            }
            else // CarryUnfinished (default): unchecked tasks keep their weekday, done ones drop
            {
                for (int i = 0; i < 7; i++)
                    foreach (TodoItem t in prev.Days[i])
                        if (!t.Done) fresh.Days[i].Add(new TodoItem { Text = t.Text });
            }
            return fresh;
        }

        /// <summary>Keep the file small: drop weeks older than ~1 year that hold nothing.</summary>
        public void Prune(DateTime currentMonday)
        {
            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, Week> kv in Weeks)
            {
                DateTime d;
                if (!DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out d)) { drop.Add(kv.Key); continue; }
                if (kv.Value.IsEmpty && d < currentMonday.AddDays(-370)) drop.Add(kv.Key);
            }
            foreach (string k in drop) Weeks.Remove(k);
        }
    }

    internal static class Store
    {
        static readonly object Gate = new object();

        public static Board Load()
        {
            Board b = new Board();
            try
            {
                if (!File.Exists(Paths.Board)) return b;
                J root = J.Parse(File.ReadAllText(Paths.Board));
                J weeks = J.Child(root, "weeks");
                if (weeks == null || weeks.Members == null) return b;

                foreach (KeyValuePair<string, J> kv in weeks.Members)
                {
                    J days = kv.Value;
                    if (days == null || days.Items == null) continue;
                    Week w = new Week();
                    for (int i = 0; i < 7 && i < days.Items.Count; i++)
                    {
                        J list = days.Items[i];
                        if (list == null || list.Items == null) continue;
                        foreach (J it in list.Items)
                        {
                            string text = J.Str(it, "text", null);
                            if (text == null) continue;
                            TodoItem t = new TodoItem();
                            t.Text = text;
                            t.Done = J.Bool(it, "done", false);
                            string at = J.Str(it, "doneAt", null);
                            DateTime parsed;
                            if (at != null && DateTime.TryParse(at, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out parsed)) t.DoneAt = parsed;
                            w.Days[i].Add(t);
                        }
                    }
                    b.Weeks[kv.Key] = w;
                }
            }
            catch { }
            return b;
        }

        public static void Save(Board b)
        {
            lock (Gate)
            {
                try
                {
                    J root = J.Obj();
                    root["version"] = J.Of(1d);
                    J weeks = J.Obj();
                    List<string> keys = new List<string>(b.Weeks.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string k in keys)
                    {
                        Week w = b.Weeks[k];
                        J days = J.Arr();
                        for (int i = 0; i < 7; i++)
                        {
                            J list = J.Arr();
                            foreach (TodoItem t in w.Days[i])
                            {
                                J o = J.Obj();
                                o["text"] = J.Of(t.Text ?? "");
                                o["done"] = J.Of(t.Done);
                                if (t.DoneAt.HasValue)
                                    o["doneAt"] = J.Of(t.DoneAt.Value.ToString("o", CultureInfo.InvariantCulture));
                                list.Add(o);
                            }
                            days.Add(list);
                        }
                        weeks[k] = days;
                    }
                    root["weeks"] = weeks;

                    // write-then-replace so a crash mid-save can't shred the board
                    string tmp = Paths.Board + ".tmp";
                    File.WriteAllText(tmp, root.ToString());
                    if (File.Exists(Paths.Board)) File.Replace(tmp, Paths.Board, null);
                    else File.Move(tmp, Paths.Board);
                }
                catch { }
            }
        }
    }
}
