using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        // Immutable-layout numeric records avoid a separate float array and
        // key/value array for each vector in every captured transform.
        public sealed class FloatArrayLayout
        {
            internal readonly string[] Names;
            internal readonly int[] Lengths;
            private readonly int count;
            public FloatArrayLayout(string[] names, int[] lengths)
            {
                if (names.Length != lengths.Length) throw new ArgumentException("Mismatched layout");
                Names = (string[])names.Clone(); Lengths = (int[])lengths.Clone();
                foreach (int length in Lengths)
                { if (length < 0) throw new ArgumentException("Negative array length"); count += length; }
            }
            public object Capture(float[] values)
            {
                if (values.Length != count) throw new ArgumentException("Mismatched numeric record");
                return new FloatArrayRecord { Layout = this, Values = values };
            }
        }
        private sealed class FloatArrayRecord
        {
            public FloatArrayLayout Layout;
            public float[] Values;
        }

        internal static bool WriteNumeric(BinaryWriter writer, object value)
        {
            var numeric = value as FloatArrayRecord;
            if (numeric == null) return false;
            writer.Write((byte)5); writer.Write(numeric.Layout.Names.Length); int offset = 0;
            for (int i = 0; i < numeric.Layout.Names.Length; i++)
            {
                WorkerWire.WriteString(writer, numeric.Layout.Names[i]);
                writer.Write((byte)7); writer.Write(numeric.Layout.Lengths[i]);
                for (int j = 0; j < numeric.Layout.Lengths[i]; j++) writer.Write(numeric.Values[offset++]);
            }
            return true;
        }

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
            var numeric = value as FloatArrayRecord;
            if (numeric != null)
            {
                output.Append('{'); int offset = 0;
                for (int field = 0; field < numeric.Layout.Names.Length; field++)
                {
                    if (field > 0) output.Append(',');
                    Quote(output, numeric.Layout.Names[field]); output.Append(":[");
                    int length = numeric.Layout.Lengths[field];
                    for (int i = 0; i < length; i++)
                    { if (i > 0) output.Append(','); WriteNumber(output, numeric.Values[offset++]); }
                    output.Append(']');
                }
                output.Append('}'); return;
            }
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
