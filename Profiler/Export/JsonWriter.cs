using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MWCFsmProfiler
{
    // Minimal streaming JSON writer (.NET 3.5 has none). Numbers always use the invariant culture: a Czech
    // Windows would otherwise write 1,5 and break every JSON reader.
    internal class JsonWriter
    {
        private readonly TextWriter w;
        private readonly Stack<bool> first = new Stack<bool>();
        private bool afterName;

        public JsonWriter(TextWriter writer)
        {
            w = writer;
        }

        public JsonWriter BeginObject() { Comma(); w.Write('{'); first.Push(true); return this; }
        public JsonWriter EndObject() { first.Pop(); w.Write('}'); return this; }
        public JsonWriter BeginArray() { Comma(); w.Write('['); first.Push(true); return this; }
        public JsonWriter EndArray() { first.Pop(); w.Write(']'); return this; }

        public JsonWriter Name(string name)
        {
            Comma();
            WriteString(name);
            w.Write(':');
            afterName = true;
            return this;
        }

        public JsonWriter Value(string s) { Comma(); if (s == null) w.Write("null"); else WriteString(s); return this; }
        public JsonWriter Value(bool b) { Comma(); w.Write(b ? "true" : "false"); return this; }
        public JsonWriter Value(int i) { Comma(); w.Write(i.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(long i) { Comma(); w.Write(i.ToString(CultureInfo.InvariantCulture)); return this; }

        public JsonWriter Value(double d, int decimals = 4)
        {
            Comma();
            if (double.IsNaN(d) || double.IsInfinity(d))
                w.Write("null");
            else
                w.Write(System.Math.Round(d, decimals).ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Prop(string name, string v) { return Name(name).Value(v); }
        public JsonWriter Prop(string name, bool v) { return Name(name).Value(v); }
        public JsonWriter Prop(string name, int v) { return Name(name).Value(v); }
        public JsonWriter Prop(string name, long v) { return Name(name).Value(v); }
        public JsonWriter Prop(string name, double v, int decimals = 4) { return Name(name).Value(v, decimals); }

        public void Raw(string text)
        {
            w.Write(text);
        }

        private void Comma()
        {
            if (afterName)
            {
                afterName = false;
                return;
            }
            if (first.Count == 0)
                return;
            if (first.Peek())
            {
                first.Pop();
                first.Push(false);
            }
            else
            {
                w.Write(',');
            }
        }

        private void WriteString(string s)
        {
            w.Write('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': w.Write("\\\""); break;
                    case '\\': w.Write("\\\\"); break;
                    case '\n': w.Write("\\n"); break;
                    case '\r': w.Write("\\r"); break;
                    case '\t': w.Write("\\t"); break;
                    // Keeps the JSON safe to embed inside a <script> tag in report.html.
                    case '<': w.Write("\\u003c"); break;
                    default:
                        if (c < ' ')
                            w.Write("\\u" + ((int)c).ToString("x4"));
                        else
                            w.Write(c);
                        break;
                }
            }
            w.Write('"');
        }
    }
}
