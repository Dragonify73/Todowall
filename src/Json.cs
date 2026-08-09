using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TodoWall
{
    internal enum JKind { Null, Bool, Number, Text, Array, Object }

    /// <summary>Minimal JSON value / parser / writer (no external deps).</summary>
    internal class J
    {
        public JKind Kind;
        public bool B;
        public double N;
        public string S;
        public List<J> Items;
        public Dictionary<string, J> Members;

        public static J Null() { J j = new J(); j.Kind = JKind.Null; return j; }
        public static J Of(bool v) { J j = new J(); j.Kind = JKind.Bool; j.B = v; return j; }
        public static J Of(double v) { J j = new J(); j.Kind = JKind.Number; j.N = v; return j; }
        public static J Of(string v)
        {
            if (v == null) return Null();
            J j = new J(); j.Kind = JKind.Text; j.S = v; return j;
        }
        public static J Arr() { J j = new J(); j.Kind = JKind.Array; j.Items = new List<J>(); return j; }
        public static J Obj()
        {
            J j = new J();
            j.Kind = JKind.Object;
            j.Members = new Dictionary<string, J>(StringComparer.OrdinalIgnoreCase);
            return j;
        }

        public J this[string key]
        {
            get
            {
                J v;
                if (Members != null && Members.TryGetValue(key, out v)) return v;
                return null;
            }
            set
            {
                if (Members == null) { Kind = JKind.Object; Members = new Dictionary<string, J>(StringComparer.OrdinalIgnoreCase); }
                Members[key] = value;
            }
        }

        public void Add(J v)
        {
            if (Items == null) { Kind = JKind.Array; Items = new List<J>(); }
            Items.Add(v);
        }

        public int Count { get { return Items == null ? 0 : Items.Count; } }
        public J At(int i) { return (Items != null && i >= 0 && i < Items.Count) ? Items[i] : null; }

        // ---- null-safe readers -------------------------------------------------
        public static string Str(J o, string key, string dflt)
        {
            if (o == null) return dflt;
            J v = o[key];
            if (v == null || v.Kind != JKind.Text) return dflt;
            return v.S;
        }
        public static double Num(J o, string key, double dflt)
        {
            if (o == null) return dflt;
            J v = o[key];
            if (v == null || v.Kind != JKind.Number) return dflt;
            return v.N;
        }
        public static bool Bool(J o, string key, bool dflt)
        {
            if (o == null) return dflt;
            J v = o[key];
            if (v == null || v.Kind != JKind.Bool) return dflt;
            return v.B;
        }
        public static J Child(J o, string key)
        {
            if (o == null) return null;
            return o[key];
        }

        // ---- writer ------------------------------------------------------------
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, this, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, J v, int indent)
        {
            if (v == null) { sb.Append("null"); return; }
            switch (v.Kind)
            {
                case JKind.Null: sb.Append("null"); break;
                case JKind.Bool: sb.Append(v.B ? "true" : "false"); break;
                case JKind.Number:
                    if (v.N == Math.Floor(v.N) && Math.Abs(v.N) < 1e15)
                        sb.Append(((long)v.N).ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append(v.N.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JKind.Text: Escape(sb, v.S); break;
                case JKind.Array:
                    if (v.Items == null || v.Items.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int i = 0; i < v.Items.Count; i++)
                    {
                        Pad(sb, indent + 1);
                        Write(sb, v.Items[i], indent + 1);
                        if (i < v.Items.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    Pad(sb, indent); sb.Append(']');
                    break;
                case JKind.Object:
                    if (v.Members == null || v.Members.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    int k = 0;
                    foreach (KeyValuePair<string, J> kv in v.Members)
                    {
                        Pad(sb, indent + 1);
                        Escape(sb, kv.Key);
                        sb.Append(": ");
                        Write(sb, kv.Value, indent + 1);
                        if (k < v.Members.Count - 1) sb.Append(',');
                        sb.Append('\n');
                        k++;
                    }
                    Pad(sb, indent); sb.Append('}');
                    break;
            }
        }

        static void Pad(StringBuilder sb, int n) { sb.Append(' ', n * 2); }

        static void Escape(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        // ---- parser ------------------------------------------------------------
        public static J Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            try
            {
                J v = ParseValue(text, ref i);
                return v;
            }
            catch { return null; }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static J ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return Of(ParseString(s, ref i));
            if (c == 't') { Expect(s, ref i, "true"); return Of(true); }
            if (c == 'f') { Expect(s, ref i, "false"); return Of(false); }
            if (c == 'n') { Expect(s, ref i, "null"); return Null(); }
            return Of(ParseNumber(s, ref i));
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("bad literal");
            i += word.Length;
        }

        static J ParseObject(string s, ref int i)
        {
            J o = Obj();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected :");
                i++;
                o[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("bad object");
            }
        }

        static J ParseArray(string s, ref int i)
        {
            J a = Arr();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("bad array");
            }
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("unterminated string");
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }
    }
}
