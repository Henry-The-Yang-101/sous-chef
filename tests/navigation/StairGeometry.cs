using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

// Independent geometric replay of the recorded floor/tread/wedge boxes.
// Conservative advancement checks continuous capsule motion against solid boxes;
// this is deliberately separate from the planner's support sampling algorithm.
internal sealed class RecordedBox : Collider
{
    public Vector3 Centre, Half;
    public Vector3[] Axes;
    private static float Axis(Vector3 v, int i) { return i == 0 ? v.x : i == 1 ? v.y : v.z; }
    public Vector3 Closest(Vector3 p)
    {
        Vector3 q = Centre;
        for (int i = 0; i < 3; i++) q += Axes[i] * Math.Clamp(Vector3.Dot(p - Centre, Axes[i]), -Axis(Half, i), Axis(Half, i));
        return q;
    }
    private float Distance(Vector3 p) { return Vector3.Distance(p, Closest(p)); }
    private Vector3 CapsuleClosest(Vector3 a, Vector3 b)
    {
        float lo = 0, hi = 1;
        for (int i = 0; i < 24; i++)
        {
            float l = (2 * lo + hi) / 3, h = (lo + 2 * hi) / 3;
            if (Distance(Vector3.Lerp(a, b, l)) < Distance(Vector3.Lerp(a, b, h))) hi = h; else lo = l;
        }
        Vector3 result = Vector3.Lerp(a, b, (lo + hi) / 2);
        if (Distance(a) < Distance(result)) result = a;
        if (Distance(b) < Distance(result)) result = b;
        return result;
    }
    public float CapsuleDistance(Vector3 a, Vector3 b) { return Distance(CapsuleClosest(a, b)); }
    public RaycastHit? Sphere(Vector3 p, float radius, Vector3 direction, float length)
    {
        float t = 0;
        for (int i = 0; i < 100 && t <= length; i++)
        {
            Vector3 at = p + direction * t;
            float gap = Distance(at) - radius;
            if (gap < .00001f)
            {
                Vector3 contact = Closest(at);
                return new RaycastHit { collider = this, point = contact, normal = (at - contact).normalized, distance = t };
            }
            t += Math.Max(.00001f, gap);
        }
        return null;
    }
    public bool Cast(Vector3 a, Vector3 b, float radius, Vector3 direction, float length)
    {
        float t = 0;
        for (int i = 0; i < 100 && t <= length; i++)
        {
            float gap = CapsuleDistance(a + direction * t, b + direction * t) - radius;
            if (gap < .00001f) return true;
            t += Math.Max(.00001f, gap);
        }
        return false;
    }
    private static Vector3 Read(JsonElement v) { return new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()); }
    public static List<RecordedBox> Install(string fixture = "wizard-stair-corner.json")
    {
        var boxes = new List<RecordedBox>();
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture))))
            foreach (var record in json.RootElement.EnumerateArray())
            {
                var m = record.GetProperty("matrix");
                var axes = new[] {
                    new Vector3(m[0].GetSingle(), m[1].GetSingle(), m[2].GetSingle()),
                    new Vector3(m[4].GetSingle(), m[5].GetSingle(), m[6].GetSingle()),
                    new Vector3(m[8].GetSingle(), m[9].GetSingle(), m[10].GetSingle()) };
                Vector3 origin = new Vector3(m[12].GetSingle(), m[13].GetSingle(), m[14].GetSingle());
                Vector3 centre = Read(record.GetProperty("center")), size = Read(record.GetProperty("size"));
                var bounds = record.GetProperty("bounds");
                boxes.Add(new GameObject(record.GetProperty("name").GetString()).Add(new RecordedBox {
                    Centre = origin + axes[0] * centre.x + axes[1] * centre.y + axes[2] * centre.z,
                    Half = new Vector3(size.x * axes[0].magnitude, size.y * axes[1].magnitude, size.z * axes[2].magnitude) * .5f,
                    Axes = axes.Select(a => a.normalized).ToArray(),
                    testBounds = new Bounds(Read(bounds.GetProperty("center")), Read(bounds.GetProperty("size"))) }));
            }
        Physics.Nearby = (p, r) => boxes.Cast<Collider>().ToArray();
        Physics.GroundSphere = (p, r, d, length) => boxes.Select(b => b.Sphere(p, r, d, length)).Where(h => h.HasValue).Select(h => h.Value).ToArray();
        Physics.Overlap = (a, b, r) => boxes.Where(c => c.CapsuleDistance(a, b) < r - .00001f).Cast<Collider>().ToArray();
        Physics.Cast = (a, b, r, d, length) => boxes.Where(c => c.Cast(a, b, r, d, length)).Select(c => new RaycastHit { collider = c }).ToArray();
        Physics.Penetration = (a, ap, ar, b, bp, br) =>
        {
            var capsule = a as CapsuleCollider; var box = b as RecordedBox;
            if (capsule == null || box == null) return false;
            Vector3 centre = ap + capsule.center;
            float halfAxis = Math.Max(0, capsule.height * .5f - capsule.radius);
            Vector3 closest = box.CapsuleClosest(centre - Vector3.up * halfAxis, centre + Vector3.up * halfAxis);
            Vector3 offset = closest - box.Closest(closest);
            Physics.PenetrationDirection = offset.normalized;
            Physics.PenetrationDistance = capsule.radius - offset.magnitude;
            return Physics.PenetrationDistance > 0;
        };
        return boxes;
    }
}
