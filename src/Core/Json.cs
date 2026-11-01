using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SousChef.Core
{
    // Only plain, already-copied values cross the capture/transport boundary.
    public static class Json
    {
        public static Dictionary<string, object> Object(params object[] pairs)
        {
            var value = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) value.Add((string)pairs[i], pairs[i + 1]);
            return value;
        }

        public static string Encode(object value)
        {
            var output = new StringBuilder(4096);
            Write(output, value);
            return output.ToString();
        }

        private static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string) { Quote(output, (string)value); return; }
            if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
            if (value is IDictionary<string, object>)
            {
                output.Append('{'); bool first = true;
                foreach (var pair in (IDictionary<string, object>)value)
                {
                    if (!first) output.Append(','); first = false;
                    Quote(output, pair.Key); output.Append(':'); Write(output, pair.Value);
                }
                output.Append('}'); return;
            }
            if (value is IEnumerable)
            {
                output.Append('['); bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) output.Append(','); first = false;
                    Write(output, item);
                }
                output.Append(']'); return;
            }
            if (value is double || value is float)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (Double.IsNaN(number) || Double.IsInfinity(number)) output.Append("null");
                else output.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong || value is decimal)
            {
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return;
            }
            throw new ArgumentException("Non-wire value: " + value.GetType().FullName);
        }

        private static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        // Escaping UTF-16 code units preserves valid pairs and lone surrogates.
                        if (c < 32 || Char.IsSurrogate(c)) output.Append("\\u").Append(((int)c).ToString("x4"));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
