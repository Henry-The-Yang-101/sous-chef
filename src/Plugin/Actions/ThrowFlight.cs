using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal static class ItemBody
    {
        public static bool Radius(GameObject item, out float radius)
        {
            radius = 0; var shape = item.GetComponent<Collider>(); if (shape == null) return false;
            Vector3 scale = shape.transform.lossyScale; scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var box = shape as BoxCollider; var sphere = shape as SphereCollider; var pill = shape as CapsuleCollider; Vector3 center;
            if (box != null) { center = box.center; radius = new Vector3(box.size.x * scale.x, box.size.y * scale.y, box.size.z * scale.z).magnitude * .5f; }
            else if (sphere != null) { center = sphere.center; radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)); }
            else if (pill != null) { center = pill.center; radius = Mathf.Max(pill.height * .5f, pill.radius) * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)); }
            else return false;
            radius = Mathf.Max(.05f, radius + Vector3.Distance(shape.transform.TransformPoint(center), item.transform.position));
            return radius <= .6f && ThrowTrajectory.Finite(radius);
        }
    }
    internal static class NativeThrow
    {
        private static readonly MethodInfo calculate = typeof(ServerAttachmentThrower).GetMethod("CalculateThrowVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Vector2) }, null);
        public static bool Velocity(PlayerControls chef, Vector3 direction, out Vector3 velocity)
        {
            velocity = Vector3.zero; var thrower = chef.GetComponent<ServerAttachmentThrower>();
            if (thrower == null || calculate == null) return false;
            Vector2 nativeDirection = new Vector2(direction.x, direction.z);
            velocity = (Vector3)calculate.Invoke(thrower, new object[] { nativeDirection });
            return ThrowTrajectory.Finite(velocity.x) && ThrowTrajectory.Finite(velocity.y) && ThrowTrajectory.Finite(velocity.z);
        }
    }

    internal sealed class WindField
    {
        private sealed class Area { public Collider[] Shapes; public Vector3 Velocity; }
        private readonly List<Area> areas = new List<Area>();
        private Collider itemShape;
        private Vector3 shapeOffset;
        private Quaternion shapeRotation;
        private float timeScale = 1;
        public bool AffectsItem { get; private set; }
        public bool HasWind { get { return AffectsItem && areas.Count > 0; } }
        public string Prepare(PlayerControls chef, GameObject item)
        {
            areas.Clear(); itemShape = item.GetComponent<Collider>();
            AffectsItem = item.GetComponent<AttachmentWindReceiver>() != null && item.GetComponent<ServerAttachmentWindReceiver>() != null;
            if (!AffectsItem) return null;
            if (itemShape == null) return "unsupported_wind_item_geometry";
            var physical = item.GetComponent<PhysicalAttachment>();
            GameObject clockObject = physical != null && physical.m_container != null ? physical.m_container.gameObject : item;
            timeScale = Time.deltaTime > .000001f ? TimeManager.GetDeltaTime(clockObject) / Time.deltaTime : 1;
            if (!ThrowTrajectory.Finite(timeScale) || timeScale < 0) return "unsupported_wind_time_scale";
            Quaternion inverse = Quaternion.Inverse(chef.transform.rotation);
            shapeOffset = inverse * (itemShape.transform.position - item.transform.position);
            shapeRotation = inverse * itemShape.transform.rotation;
            foreach (WindVolume volume in UnityEngine.Object.FindObjectsOfType<WindVolume>())
            {
                if (!volume.isActiveAndEnabled || (ActionObservations.Read<LayerMask>(volume, "m_windFilter").value & (1 << item.layer)) == 0) continue;
                var colliders = new List<Collider>();
                foreach (Collider shape in volume.GetComponents<Collider>())
                    if (shape.enabled && shape.gameObject.activeInHierarchy) colliders.Add(shape);
                if (colliders.Count == 0) continue;
                if (areas.Count >= 64) return "too_many_wind_volumes";
                areas.Add(new Area { Shapes = colliders.ToArray(), Velocity = volume.GetVelocity() });
            }
            return null;
        }
        public ulong At(Vector3 root, Vector3 direction)
        {
            if (!AffectsItem || areas.Count == 0) return 0;
            Quaternion facing = Quaternion.LookRotation(direction);
            Vector3 position = root + facing * shapeOffset; Quaternion rotation = facing * shapeRotation;
            ulong result = 0;
            for (int i = 0; i < areas.Count; i++)
                foreach (Collider area in areas[i].Shapes)
                {
                    Vector3 separation; float distance;
                    if (Physics.ComputePenetration(itemShape, position, rotation, area, area.transform.position, area.transform.rotation, out separation, out distance))
                    { result |= 1UL << i; break; }
                }
            return result;
        }
        public Vector3 Velocity(ulong membership)
        {
            Vector3 result = Vector3.zero;
            for (int i = 0; i < areas.Count; i++) if ((membership & (1UL << i)) != 0) result += areas[i].Velocity;
            // ServerAttachmentWindReceiver.FixedUpdate uses this squared-speed threshold.
            return result.sqrMagnitude < .05f ? Vector3.zero : result * timeScale;
        }
        public Vector3 ExitVelocity(ulong previous, ulong current)
        {
            Vector3 result = Vector3.zero; ulong exited = previous & ~current;
            for (int i = 0; i < areas.Count; i++) if ((exited & (1UL << i)) != 0) result += areas[i].Velocity;
            return result;
        }
    }

    internal sealed class FlightPath
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public readonly List<float> Times = new List<float>();
        public Vector3 End, InitialVelocity, FinalVelocity, WindDisplacement;
        public float PeakHeight;
    }

    internal static class ThrowFlight
    {
        public static FlightPath Simulate(Vector3 origin, Vector3 direction, Vector3 launch, Vector3 gravity, float drag, float time, float step, WindField wind)
        {
            var result = new FlightPath { InitialVelocity = launch, PeakHeight = origin.y };
            Vector3 position = origin, velocity = launch; ulong membership = wind.At(position, direction);
            result.Points.Add(position); result.Times.Add(0);
            int count = Mathf.CeilToInt(time / step);
            if (count < 1 || count > 4000) throw new InvalidOperationException("unsupported_flight_duration");
            float damping = Mathf.Max(0, 1 - drag * step), elapsed = 0;
            for (int i = 0; i < count; i++)
            {
                float duration = Mathf.Min(step, time - elapsed);
                if (duration <= 0) break;
                Vector3 drift = wind.Velocity(membership);
                velocity = (velocity + gravity * step) * damping;
                Vector3 next = position + (velocity + drift) * duration;
                ulong nextMembership = wind.At(next, direction);
                velocity += wind.ExitVelocity(membership, nextMembership);
                membership = nextMembership; position = next; elapsed += duration;
                result.WindDisplacement += drift * duration;
                result.PeakHeight = Mathf.Max(result.PeakHeight, position.y);
                result.Points.Add(position); result.Times.Add(elapsed);
            }
            result.End = position; result.FinalVelocity = velocity; return result;
        }
    }
}
