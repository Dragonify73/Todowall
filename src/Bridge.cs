using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace TodoWall
{
    /// <summary>The way in for other programs: a local named pipe that takes one JSON
    /// request per connection and answers with one JSON reply.
    ///
    /// The board lives in memory and is written out whole on every edit, so nothing else
    /// can safely touch board.json while the app is up. Anything that wants to add a task
    /// - the Claude Desktop extension under .\extension is the first - therefore talks to
    /// the running app instead, and the app applies the change on its own UI thread, the
    /// same way a click would.
    ///
    /// Wire format, one line each way, UTF-8:
    ///   { "op": "ping" }
    ///   { "op": "list", "from": "2026-09-21", "to": "2026-10-04" }
    ///   { "op": "add", "items": [ { "date": "2026-09-25", "text": "Buy milk" } ] }
    ///   { "op": "complete", "date": "2026-09-25", "index": 0, "text": "Buy milk", "done": true }
    ///   { "op": "remove",   "date": "2026-09-25", "index": 0, "text": "Buy milk" }
    ///   { "op": "move",     "date": "2026-09-25", "index": 0, "text": "Buy milk", "to": "2026-09-26" }
    /// Tasks have no ids; a task is addressed by its day and position, and the text is
    /// sent along as a check so a board that moved underneath the caller is not edited
    /// blind - when the text no longer matches at that index, the day is searched for it.</summary>
    internal static class Bridge
    {
        public const string PipeName = "TodoWall.Bridge";
        const int MaxRequestBytes = 256 * 1024;
        const int MaxTextLength = 2000;

        static Thread _thread;
        static volatile bool _stop;
        static NamedPipeServerStream _current;

        public static void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Serve);
            _thread.IsBackground = true;
            _thread.Name = "TodoWall.Bridge";
            _thread.Start();
        }

        public static void Stop()
        {
            _stop = true;
            try { NamedPipeServerStream s = _current; if (s != null) s.Dispose(); } catch { }
        }

        /// <summary>Accept loop. Each connection is handed to the pool the moment it
        /// arrives and a fresh instance goes up to listen, so two callers at once - Claude
        /// issuing parallel tool calls - both get through instead of one being refused.
        /// The mutations themselves are serialised anyway: every one runs on the UI thread.</summary>
        static void Serve()
        {
            while (!_stop)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, MaxRequestBytes, MaxRequestBytes);
                    _current = server;
                    server.WaitForConnection();
                    _current = null;
                    if (_stop) break;

                    NamedPipeServerStream accepted = server;
                    server = null;   // now owned by the worker
                    ThreadPool.QueueUserWorkItem(delegate { Converse(accepted); });
                }
                catch (Exception ex)
                {
                    if (_stop) break;
                    Log.Write("bridge: " + ex.GetType().Name + " " + ex.Message);
                    Thread.Sleep(250);   // never spin on a broken pipe handle
                }
                finally
                {
                    _current = null;
                    if (server != null) { try { server.Dispose(); } catch { } }
                }
            }
        }

        /// <summary>One request, one reply, close.</summary>
        static void Converse(NamedPipeServerStream pipe)
        {
            try
            {
                string request = ReadLine(pipe);
                string reply = request == null
                    ? Error("empty request").ToString()
                    : Handle(request);

                // J pretty-prints; the wire is one line per message. Line breaks inside
                // strings are already escaped, so the only ones left are formatting.
                reply = reply.Replace("\r", "").Replace("\n", "");
                byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                try { pipe.WaitForPipeDrain(); } catch { }
                try { pipe.Disconnect(); } catch { }
            }
            catch (Exception ex)
            {
                if (!_stop) Log.Write("bridge: " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { pipe.Dispose(); } catch { }
            }
        }

        static string ReadLine(Stream s)
        {
            MemoryStream buf = new MemoryStream();
            byte[] one = new byte[1];
            while (buf.Length < MaxRequestBytes)
            {
                int n = s.Read(one, 0, 1);
                if (n <= 0) break;
                if (one[0] == (byte)'\n') break;
                buf.WriteByte(one[0]);
            }
            if (buf.Length == 0) return null;
            return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
        }

        /// <summary>Parse on this thread, mutate on the UI thread, serialise the answer here.</summary>
        static string Handle(string request)
        {
            J req;
            try { req = J.Parse(request); }
            catch (Exception ex) { return Error("bad json: " + ex.Message).ToString(); }
            if (req == null || req.Members == null) return Error("request must be an object").ToString();

            string op = J.Str(req, "op", "");
            if (op == "ping") return Ping().ToString();

            Application app = Application.Current;
            if (app == null) return Error("app is shutting down").ToString();

            J result = null;
            try
            {
                app.Dispatcher.Invoke(new Action(delegate
                {
                    try { result = Dispatch(op, req); }
                    catch (Exception ex) { result = Error(ex.Message); }
                }));
            }
            catch (Exception ex) { result = Error("dispatch failed: " + ex.Message); }
            return (result ?? Error("no result")).ToString();
        }

        // ================================================================= operations

        static J Ping()
        {
            J o = Ok();
            o["app"] = J.Of("TodoWall");
            o["version"] = J.Of(typeof(Bridge).Assembly.GetName().Version.ToString(3));
            o["today"] = J.Of(Board.Key(DateTime.Today));
            return o;
        }

        static J Dispatch(string op, J req)
        {
            switch (op)
            {
                case "list": return List(req);
                case "add": return Add(req);
                case "complete": return Complete(req);
                case "remove": return Remove(req);
                case "move": return Move(req);
                default: return Error("unknown op '" + op + "'");
            }
        }

        static J List(J req)
        {
            DateTime todayMonday = Board.MondayOf(DateTime.Today);
            DateTime from, to;
            if (!TryDate(J.Str(req, "from", null), out from)) from = todayMonday;
            if (!TryDate(J.Str(req, "to", null), out to)) to = todayMonday.AddDays(13);
            from = from.Date; to = to.Date;
            if (to < from) { DateTime t = from; from = to; to = t; }
            if ((to - from).Days > 370) to = from.AddDays(370);

            J days = J.Arr();
            for (DateTime monday = Board.MondayOf(from); monday <= to; monday = monday.AddDays(7))
            {
                Week w = WeekFor(monday, false);
                for (int d = 0; d < 7; d++)
                {
                    DateTime date = monday.AddDays(d);
                    if (date < from || date > to) continue;
                    J day = J.Obj();
                    day["date"] = J.Of(Board.Key(date));
                    day["weekday"] = J.Of(date.DayOfWeek.ToString());
                    J tasks = J.Arr();
                    if (w != null)
                        for (int i = 0; i < w.Days[d].Count; i++)
                            tasks.Add(TaskJson(w.Days[d][i], i));
                    day["tasks"] = tasks;
                    days.Add(day);
                }
            }

            J o = Ok();
            o["today"] = J.Of(Board.Key(DateTime.Today));
            o["from"] = J.Of(Board.Key(from));
            o["to"] = J.Of(Board.Key(to));
            o["days"] = days;
            return o;
        }

        static J Add(J req)
        {
            J items = J.Child(req, "items");
            if (items == null || items.Items == null || items.Items.Count == 0)
                return Error("'items' must be a non-empty array of { date, text }");
            if (items.Items.Count > 200) return Error("too many items in one request (max 200)");

            // Validate everything before touching the board, so a bad entry rejects the
            // whole batch rather than leaving half of it behind.
            List<KeyValuePair<DateTime, string>> parsed = new List<KeyValuePair<DateTime, string>>();
            for (int i = 0; i < items.Items.Count; i++)
            {
                J it = items.Items[i];
                DateTime date;
                string text = CleanText(J.Str(it, "text", null));
                if (text == null) return Error("items[" + i + "]: 'text' is required");
                if (!TryDate(J.Str(it, "date", null), out date)) return Error("items[" + i + "]: 'date' must be YYYY-MM-DD");
                parsed.Add(new KeyValuePair<DateTime, string>(date, text));
            }

            J added = J.Arr();
            HashSet<DateTime> touched = new HashSet<DateTime>();
            foreach (KeyValuePair<DateTime, string> kv in parsed)
            {
                DateTime monday = Board.MondayOf(kv.Key);
                int day = DayIndex(kv.Key);
                Week w = WeekFor(monday, true);
                TodoItem t = new TodoItem { Text = kv.Value };
                w.Days[day].Add(t);
                J a = TaskJson(t, w.Days[day].Count - 1);
                a["date"] = J.Of(Board.Key(kv.Key));
                added.Add(a);
                touched.Add(kv.Key);
            }

            Core.SaveBoardSoon();
            foreach (DateTime d in touched) Refresh(d, true);

            J o = Ok();
            o["added"] = added;
            return o;
        }

        static J Complete(J req)
        {
            DateTime date; Week w; int day, index;
            J err = Locate(req, out date, out w, out day, out index);
            if (err != null) return err;

            TodoItem t = w.Days[day][index];
            bool done = J.Bool(req, "done", true);
            t.Done = done;
            t.DoneAt = done ? (DateTime?)DateTime.Now : null;

            Core.SaveBoardSoon();
            Refresh(date, false);

            J o = Ok();
            J tj = TaskJson(t, index); tj["date"] = J.Of(Board.Key(date));
            o["task"] = tj;
            return o;
        }

        static J Remove(J req)
        {
            DateTime date; Week w; int day, index;
            J err = Locate(req, out date, out w, out day, out index);
            if (err != null) return err;

            TodoItem t = w.Days[day][index];
            w.Days[day].RemoveAt(index);

            Core.SaveBoardSoon();
            Refresh(date, false);

            J o = Ok();
            J tj = TaskJson(t, index); tj["date"] = J.Of(Board.Key(date));
            o["removed"] = tj;
            return o;
        }

        static J Move(J req)
        {
            DateTime date; Week w; int day, index;
            J err = Locate(req, out date, out w, out day, out index);
            if (err != null) return err;

            DateTime target;
            if (!TryDate(J.Str(req, "to", null), out target)) return Error("'to' must be YYYY-MM-DD");

            TodoItem t = w.Days[day][index];
            w.Days[day].RemoveAt(index);
            Week dst = WeekFor(Board.MondayOf(target), true);
            int tday = DayIndex(target);
            dst.Days[tday].Add(t);

            Core.SaveBoardSoon();
            Refresh(date, false);
            Refresh(target, true);

            J o = Ok();
            J tj = TaskJson(t, dst.Days[tday].Count - 1); tj["date"] = J.Of(Board.Key(target));
            o["task"] = tj;
            return o;
        }

        // ================================================================= helpers

        /// <summary>Resolve { date, index, text? } to a task, or explain why not.</summary>
        static J Locate(J req, out DateTime date, out Week w, out int day, out int index)
        {
            w = null; day = 0; index = -1;
            if (!TryDate(J.Str(req, "date", null), out date)) return Error("'date' must be YYYY-MM-DD");

            w = WeekFor(Board.MondayOf(date), false);
            day = DayIndex(date);
            if (w == null || w.Days[day].Count == 0) return Error("no tasks on " + Board.Key(date));

            List<TodoItem> list = w.Days[day];
            int idx = (int)J.Num(req, "index", -1);
            string text = CleanText(J.Str(req, "text", null));

            if (idx >= 0 && idx < list.Count && (text == null || list[idx].Text == text))
            {
                index = idx;
                return null;
            }
            if (text != null)
            {
                // The board moved under the caller; find the task by what it says instead.
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Text == text) { index = i; return null; }
                for (int i = 0; i < list.Count; i++)
                    if (string.Equals(list[i].Text, text, StringComparison.OrdinalIgnoreCase)) { index = i; return null; }
                return Error("no task '" + text + "' on " + Board.Key(date));
            }
            return Error("index " + idx + " is out of range on " + Board.Key(date) + " (" + list.Count + " tasks)");
        }

        /// <summary>Same materialisation rule as the wall: the current week is created through
        /// the rollover policy so a task added from outside does not pre-empt it.</summary>
        static Week WeekFor(DateTime monday, bool create)
        {
            DateTime todayMonday = Board.MondayOf(DateTime.Today);
            if (monday == todayMonday)
                return Core.Data.EnsureCurrentWeek(todayMonday, Core.Config.Rollover);
            return Core.Data.Get(monday, create);
        }

        static void Refresh(DateTime date, bool animateNewRow)
        {
            WallWindow wall = Core.Wall;
            if (wall != null) wall.RefreshDay(date, animateNewRow);
        }

        static int DayIndex(DateTime d) { return ((int)d.DayOfWeek + 6) % 7; }

        static bool TryDate(string s, out DateTime d)
        {
            d = default(DateTime);
            if (string.IsNullOrEmpty(s)) return false;
            return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out d);
        }

        static string CleanText(string s)
        {
            if (s == null) return null;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return null;
            if (s.Length > MaxTextLength) s = s.Substring(0, MaxTextLength);
            return s;
        }

        static J TaskJson(TodoItem t, int index)
        {
            J o = J.Obj();
            o["index"] = J.Of((double)index);
            o["text"] = J.Of(t.Text ?? "");
            o["done"] = J.Of(t.Done);
            if (t.DoneAt.HasValue)
                o["doneAt"] = J.Of(t.DoneAt.Value.ToString("o", CultureInfo.InvariantCulture));
            return o;
        }

        static J Ok() { J o = J.Obj(); o["ok"] = J.Of(true); return o; }

        static J Error(string message)
        {
            J o = J.Obj();
            o["ok"] = J.Of(false);
            o["error"] = J.Of(message ?? "error");
            return o;
        }
    }
}
