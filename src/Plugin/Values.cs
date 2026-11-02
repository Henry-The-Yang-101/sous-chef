using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal static class Values
    {
        private static readonly Json.FloatArrayLayout transformLayout = new Json.FloatArrayLayout(
            new[] { "position", "rotation", "localPosition", "localRotation", "localScale", "lossyScale", "localToWorld" },
            new[] { 3, 4, 3, 4, 3, 3, 16 });
        public static float[] Vector(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        public static float[] Rotation(Quaternion q) { return new[] { q.x, q.y, q.z, q.w }; }
        public static object Bounds(Bounds b) { return Json.Object("center", Vector(b.center), "size", Vector(b.size)); }
        public static float[] Matrix(Matrix4x4 m)
        {
            var result = new float[16];
            for (int i = 0; i < 16; i++) result[i] = m[i];
            return result;
        }
        public static object Transform(Transform t)
        {
            var values = new float[36];
            Copy(t.position, values, 0); Copy(t.rotation, values, 3);
            Copy(t.localPosition, values, 7); Copy(t.localRotation, values, 10);
            Copy(t.localScale, values, 14); Copy(t.lossyScale, values, 17);
            Matrix4x4 matrix = t.localToWorldMatrix;
            for (int i = 0; i < 16; i++) values[20 + i] = matrix[i];
            return transformLayout.Capture(values);
        }
        private static void Copy(Vector3 value, float[] output, int at)
        { output[at] = value.x; output[at + 1] = value.y; output[at + 2] = value.z; }
        private static void Copy(Quaternion value, float[] output, int at)
        { output[at] = value.x; output[at + 1] = value.y; output[at + 2] = value.z; output[at + 3] = value.w; }
    }
}
