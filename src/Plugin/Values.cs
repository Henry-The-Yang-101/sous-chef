using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal static class Values
    {
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
            return Json.Object("position", Vector(t.position), "rotation", Rotation(t.rotation),
                "localPosition", Vector(t.localPosition), "localRotation", Rotation(t.localRotation),
                "localScale", Vector(t.localScale), "lossyScale", Vector(t.lossyScale),
                "localToWorld", Matrix(t.localToWorldMatrix));
        }
    }
}
