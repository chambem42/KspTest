using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPUITest
{
    /// <summary>Tiny JSON writer for result.json (KSP ships no JSON library). Handles the few shapes we emit.</summary>
    public static class Json
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteDouble(sb, f); break;
                case double d: WriteDouble(sb, d); break;
                case IDictionary<string, object> dict: WriteObject(sb, dict, indent); break;
                case IEnumerable list: WriteArray(sb, list, indent); break;
                default: WriteString(sb, v.ToString()); break;
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, int indent)
        {
            sb.Append("{\n");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(",\n");
                first = false;
                Indent(sb, indent + 1);
                WriteString(sb, kv.Key);
                sb.Append(": ");
                WriteValue(sb, kv.Value, indent + 1);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, int indent)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                sb.Append(first ? "\n" : ",\n");
                first = false;
                Indent(sb, indent + 1);
                WriteValue(sb, item, indent + 1);
            }
            if (!first)
            {
                sb.Append('\n');
                Indent(sb, indent);
            }
            sb.Append(']');
        }

        private static void Indent(StringBuilder sb, int n)
        {
            sb.Append(' ', n * 2);
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
