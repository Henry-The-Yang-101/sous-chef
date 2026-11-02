using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;

namespace SousChef.Core
{
    // Private lossless IPC, independent of Unity and of the public JSON protocol.
    // Writers run on transport threads, after publication of frozen values.
    public static class WorkerWire
    {
        public const int Version = 1, Limit = 64 * 1024 * 1024;
        public static void WriteValue(BinaryWriter w, object value)
        {
            if (value == null) { w.Write((byte)0); return; }
            if (value is string) { w.Write((byte)1); WriteString(w, (string)value); return; }
            if (value is bool) { w.Write((byte)2); w.Write((bool)value); return; }
            if (value is double || value is float) { w.Write((byte)3); w.Write(Convert.ToDouble(value, CultureInfo.InvariantCulture)); return; }
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
            { w.Write((byte)4); w.Write(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (Json.WriteNumeric(w, value)) return;
            var record = value as Json.Record;
            if (record != null)
            {
                w.Write((byte)5); w.Write(record.Length / 2);
                for (int i = 0; i < record.Length; i += 2) { WriteString(w, (string)record.Pairs[i]); WriteValue(w, record.Pairs[i + 1]); }
                return;
            }
            var map = value as IDictionary<string, object>;
            if (map != null)
            { w.Write((byte)5); w.Write(map.Count); foreach (var p in map) { WriteString(w, p.Key); WriteValue(w, p.Value); } return; }
            var floats = value as float[];
            if (floats != null) { w.Write((byte)7); w.Write(floats.Length); foreach (float f in floats) w.Write(f); return; }
            var ints = value as int[];
            if (ints != null) { w.Write((byte)8); w.Write(ints.Length); foreach (int i in ints) w.Write(i); return; }
            var list = value as ICollection;
            if (list != null) { w.Write((byte)6); w.Write(list.Count); foreach (object x in list) WriteValue(w, x); return; }
            throw new ArgumentException("Non-wire value: " + value.GetType().FullName);
        }
        public static object ReadValue(BinaryReader r) { return ReadValue(r, 0); }
        private static object ReadValue(BinaryReader r, int depth)
        {
            if (depth > 128) throw new IOException("IPC nesting limit");
            byte tag = r.ReadByte();
            switch (tag)
            {
                case 0: return null;
                case 1: return ReadString(r);
                case 2: return r.ReadBoolean();
                case 3: return r.ReadDouble();
                case 4: return Decimal.Parse(r.ReadString(), CultureInfo.InvariantCulture);
                case 5:
                    int fields = Count(r, 1000000); var pairs = new object[fields * 2];
                    for (int i = 0; i < pairs.Length; i += 2) { pairs[i] = ReadString(r); pairs[i + 1] = ReadValue(r, depth + 1); }
                    return Json.Object(pairs);
                case 6:
                    var array = new object[Count(r, 4000000)];
                    for (int i = 0; i < array.Length; i++) array[i] = ReadValue(r, depth + 1); return array;
                case 7:
                    var floats = new float[Count(r, 16000000)]; for (int i = 0; i < floats.Length; i++) floats[i] = r.ReadSingle(); return floats;
                case 8:
                    var ints = new int[Count(r, 16000000)]; for (int i = 0; i < ints.Length; i++) ints[i] = r.ReadInt32(); return ints;
                default: throw new IOException("Unknown IPC tag");
            }
        }
        // Preserve UTF-16 code units, including lone surrogates, exactly as Json does.
        public static void WriteString(BinaryWriter w, string s) { w.Write(s.Length); foreach (char c in s) w.Write((ushort)c); }
        public static string ReadString(BinaryReader r)
        { var chars = new char[Count(r, Limit / 2)]; for (int i = 0; i < chars.Length; i++) chars[i] = (char)r.ReadUInt16(); return new string(chars); }
        public static int Count(BinaryReader r, int max)
        { int count = r.ReadInt32(); if (count < 0 || count > max) throw new IOException("IPC size limit"); return count; }
        public static void Cell(BinaryWriter w, WalkCell c) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); w.Write(c.SurfaceHistory); w.Write(c.Portal); }
        public static WalkCell Cell(BinaryReader r) { return new WalkCell(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()); }
        public static void Edges(BinaryWriter w, WalkEdge[] edges) { w.Write(edges.Length); foreach (var e in edges) { Cell(w, e.Cell); w.Write(e.Distance); } }
        public static WalkEdge[] Edges(BinaryReader r)
        { var edges = new WalkEdge[Count(r, 20000)]; for (int i = 0; i < edges.Length; i++) edges[i] = new WalkEdge(Cell(r), r.ReadSingle()); return edges; }
    }
}
