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
        // Capture already allocates the key/value array. Keep it as the detached
        // record instead of also allocating and growing a hash table for every
        // transform, component and reference. Mutations finish before publication.
        public sealed class Record
        {
            internal object[] Pairs;
            internal int Length;
            internal Record(object[] pairs) { Pairs = pairs; Length = pairs.Length; }
            public object this[string key]
            {
                get
                {
                    for (int i = 0; i < Length; i += 2)
                        if ((string)Pairs[i] == key) return Pairs[i + 1];
                    throw new KeyNotFoundException(key);
                }
                set
                {
                    for (int i = 0; i < Length; i += 2)
                        if ((string)Pairs[i] == key) { Pairs[i + 1] = value; return; }
                    if (Length == Pairs.Length) Array.Resize(ref Pairs, Length + 8);
                    Pairs[Length++] = key; Pairs[Length++] = value;
                }
            }
        }

        public static Record Object(params object[] pairs) { return new Record(pairs); }

        public static string Encode(object value)
        {
            var output = new StringBuilder(4096);
            Write(output, value);
            return output.ToString();
        }

        // Append the NDJSON delimiter in the final byte buffer, not by copying
        // an entire multi-megabyte UTF-16 snapshot to concatenate one character.
        public static byte[] Utf8Line(string value)
        {
            int length = Encoding.UTF8.GetByteCount(value);
            var bytes = new byte[length + 1];
            Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
            bytes[length] = (byte)'\n';
            return bytes;
        }

        private static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string) { Quote(output, (string)value); return; }
            if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
            var record = value as Record;
            if (record != null)
            {
                output.Append('{');
                for (int i = 0; i < record.Length; i += 2)
                {
                    if (i > 0) output.Append(',');
                    Quote(output, (string)record.Pairs[i]); output.Append(':'); Write(output, record.Pairs[i + 1]);
                }
                output.Append('}'); return;
            }
            // Unity transforms/meshes contain many primitive arrays. Enumerating
            // them as IEnumerable boxes every scalar on the encoder thread and
            // still contributes to the game's shared managed GC pressure.
            var floats = value as float[];
            if (floats != null)
            {
                output.Append('[');
                for (int i = 0; i < floats.Length; i++)
                { if (i > 0) output.Append(','); WriteNumber(output, floats[i]); }
                output.Append(']'); return;
            }
            var integers = value as int[];
            if (integers != null)
            {
                output.Append('[');
                for (int i = 0; i < integers.Length; i++)
                { if (i > 0) output.Append(','); output.Append(integers[i].ToString(CultureInfo.InvariantCulture)); }
                output.Append(']'); return;
            }
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
                WriteNumber(output, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong || value is decimal)
            {
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return;
            }
            throw new ArgumentException("Non-wire value: " + value.GetType().FullName);
        }

        private static void WriteNumber(StringBuilder output, double number)
        {
            if (Double.IsNaN(number) || Double.IsInfinity(number)) output.Append("null");
            else output.Append(number.ToString("R", CultureInfo.InvariantCulture));
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
