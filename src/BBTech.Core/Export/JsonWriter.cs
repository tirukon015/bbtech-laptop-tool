using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BBTech.Core.Export
{
    /// <summary>
    /// Minimal, dependency-free JSON serializer for dictionaries, lists and primitives.
    /// Keeps the shipped tool a single executable (no Newtonsoft/System.Text.Json DLLs).
    /// Use an ordered list of key/value pairs (JsonObject) to control key order.
    /// </summary>
    public sealed class JsonObject : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();
        public void Add(string key, object value) => _items.Add(new KeyValuePair<string, object>(key, value));
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    public static class JsonWriter
    {
        public static string Serialize(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, bool indent, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: WriteString(sb, dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset dto: WriteString(sb, dto.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("0.###", CultureInfo.InvariantCulture)); return;
                case float f: Write(sb, (double)f, indent, level); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int _: case long _: case short _: case byte _: case uint _: case ulong _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case JsonObject o: WriteObject(sb, o, indent, level); return;
                case IDictionary<string, object> dict:
                    var jo = new JsonObject();
                    foreach (var kv in dict) jo.Add(kv.Key, kv.Value);
                    WriteObject(sb, jo, indent, level);
                    return;
                case IEnumerable list: WriteArray(sb, list, indent, level); return;
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        private static void WriteObject(StringBuilder sb, JsonObject o, bool indent, int level)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in o)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indent, level + 1);
                WriteString(sb, kv.Key);
                sb.Append(indent ? ": " : ":");
                Write(sb, kv.Value, indent, level + 1);
            }
            if (!first) NewLine(sb, indent, level);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, bool indent, int level)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indent, level + 1);
                Write(sb, item, indent, level + 1);
            }
            if (!first) NewLine(sb, indent, level);
            sb.Append(']');
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
