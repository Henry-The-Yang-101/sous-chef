using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Query the native primitive at its real offset from the item pivot. Held
    // collider bounds can be disabled/empty, so derive geometry from shape data.
    internal sealed class ItemBody
    {
        private int kind;
        private Vector3 half, offset, axis;
        private Quaternion relativeRotation;
        private float radius, segment, nativeRadius, nativeHeight;
        private Vector3 nativeCenter, signedScale, pivotOffset;
        public static int DetachedLayer(GameObject item)
        {
            var swapper = item.GetComponent<PlacementLayerSwapper>();
            int layer = swapper == null ? item.layer : ActionObservations.Read<int>(swapper, "m_defaultLayerId");
            if (layer < 0 || layer > 31) throw new InvalidOperationException("unsupported_detached_item_layer");
            return layer;
        }
        private static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z)); }
        private static bool Finite(Vector3 v) { return ThrowTrajectory.Finite(v.x) && ThrowTrajectory.Finite(v.y) && ThrowTrajectory.Finite(v.z); }
        public bool Prepare(PlayerControls chef, GameObject item)
        {
            var shape = item.GetComponent<Collider>(); if (shape == null) return false;
            Vector3 scale = Abs(shape.transform.lossyScale), center;
            if (!Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0) return false;
            var box = shape as BoxCollider; var sphere = shape as SphereCollider; var capsule = shape as CapsuleCollider;
            if (box != null)
            {
                kind = 0; center = box.center;
                half = new Vector3(box.size.x * scale.x, box.size.y * scale.y, box.size.z * scale.z) * .5f;
                if (!Finite(half) || half.x <= 0 || half.y <= 0 || half.z <= 0) return false;
            }
            else if (sphere != null)
            {
                kind = 1; center = sphere.center; radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                if (!ThrowTrajectory.Finite(radius) || radius <= 0) return false;
            }
            else if (capsule != null)
            {
                kind = 2; center = capsule.center;
                if (capsule.direction < 0 || capsule.direction > 2 || !ThrowTrajectory.Finite(capsule.height) || capsule.height <= 0) return false;
                axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                float along = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
                float across = capsule.direction == 0 ? Mathf.Max(scale.y, scale.z) : capsule.direction == 1 ? Mathf.Max(scale.x, scale.z) : Mathf.Max(scale.x, scale.y);
                radius = capsule.radius * across; segment = Mathf.Max(0, capsule.height * along * .5f - radius);
                if (!ThrowTrajectory.Finite(radius) || radius <= 0 || !ThrowTrajectory.Finite(segment)) return false;
            }
            else return false;
            if (!Finite(center)) return false;
            nativeCenter = center; signedScale = shape.transform.lossyScale;
            nativeRadius = sphere != null ? sphere.radius : capsule != null ? capsule.radius : 0;
            nativeHeight = capsule != null ? capsule.height : 0;
            Quaternion inverse = Quaternion.Inverse(chef.transform.rotation);
            pivotOffset = inverse * (shape.transform.position - item.transform.position);
            offset = inverse * (shape.transform.TransformPoint(center) - item.transform.position);
            relativeRotation = inverse * shape.transform.rotation;
            return Finite(offset);
        }
        public Bounds BoundsAt(Vector3 origin, Vector3 direction)
        {
            Quaternion facing = Quaternion.LookRotation(direction), rotation = facing * relativeRotation;
            Vector3 extent = kind == 0 ? Abs(rotation * Vector3.right) * half.x + Abs(rotation * Vector3.up) * half.y + Abs(rotation * Vector3.forward) * half.z :
                kind == 1 ? Vector3.one * radius : Abs(rotation * axis) * segment + Vector3.one * radius;
            return new Bounds(origin + facing * offset, extent * 2);
        }
        public Vector3 CatchScanPoint(Vector3 origin, Vector3 direction, Vector3 point)
        {
            Quaternion facing = Quaternion.LookRotation(direction), rotation = facing * relativeRotation;
            Vector3 center = origin + facing * offset;
            // Mirror InteractWithItemHelper.GetClosestPointOnSurface without
            // moving the real collider to a hypothetical flight position.
            if (kind == 0) return BoundsAt(origin, direction).ClosestPoint(point);
            if (kind == 1) return center + (point - center).normalized * nativeRadius;
            Vector3 pivot = origin + facing * pivotOffset;
            Vector3 local = Quaternion.Inverse(rotation) * (point - pivot);
            local = new Vector3(local.x / signedScale.x, local.y / signedScale.y, local.z / signedScale.z) - nativeCenter;
            float halfHeight = Mathf.Max(0, nativeHeight - 2 * nativeRadius) * .5f;
            Vector3 axial = Vector3.up * Mathf.Clamp(local.y, -halfHeight, halfHeight);
            Vector3 surface = axial + (local - axial).normalized * nativeRadius + nativeCenter;
            return pivot + rotation * new Vector3(surface.x * signedScale.x, surface.y * signedScale.y, surface.z * signedScale.z);
        }
        public Bounds Receiving(Bounds receiver, Vector3 direction)
        {
            Bounds shape = BoundsAt(Vector3.zero, direction);
            Vector3 center = receiver.center - shape.center; center.y = receiver.max.y - shape.min.y;
            // Native catchers accept an item centered over the receiving top;
            // food need not fit wholly inside its footprint. The exact body
            // sweeps still reject collisions with neighbouring stations/walls.
            return new Bounds(center, new Vector3(Mathf.Max(.02f, receiver.size.x - .1f), .16f, Mathf.Max(.02f, receiver.size.z - .1f)));
        }
        public int Overlap(Vector3 origin, Vector3 direction, Collider[] output, int mask, float margin, float horizontalUncertainty = 0)
        {
            if (horizontalUncertainty > 0)
            {
                Bounds bounds = BoundsAt(origin, direction);
                Vector3 extra = new Vector3(margin + horizontalUncertainty, margin, margin + horizontalUncertainty);
                return Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + extra, output, Quaternion.LookRotation(Vector3.forward), mask, QueryTriggerInteraction.Collide);
            }
            Quaternion facing = Quaternion.LookRotation(direction), rotation = facing * relativeRotation;
            Vector3 center = origin + facing * offset;
            if (kind == 0) return Physics.OverlapBoxNonAlloc(center, half + Vector3.one * margin, output, rotation, mask, QueryTriggerInteraction.Collide);
            if (kind == 1) return Physics.OverlapSphereNonAlloc(center, radius + margin, output, mask, QueryTriggerInteraction.Collide);
            Vector3 end = rotation * axis * segment;
            return Physics.OverlapCapsuleNonAlloc(center - end, center + end, radius + margin, output, mask, QueryTriggerInteraction.Collide);
        }
        public int Cast(Vector3 origin, Vector3 direction, Vector3 movement, RaycastHit[] output, int mask, float margin, Vector3 horizontalUncertainty = default(Vector3))
        {
            if (movement.sqrMagnitude < .00000001f) return 0;
            if (horizontalUncertainty.sqrMagnitude > 0)
            {
                Bounds bounds = BoundsAt(origin, direction);
                Vector3 extra = new Vector3(margin + horizontalUncertainty.x, margin, margin + horizontalUncertainty.z);
                return Physics.BoxCastNonAlloc(bounds.center, bounds.extents + extra, movement.normalized, output, Quaternion.LookRotation(Vector3.forward), movement.magnitude, mask, QueryTriggerInteraction.Collide);
            }
            Quaternion facing = Quaternion.LookRotation(direction), rotation = facing * relativeRotation;
            Vector3 center = origin + facing * offset;
            if (kind == 0) return Physics.BoxCastNonAlloc(center, half + Vector3.one * margin, movement.normalized, output, rotation, movement.magnitude, mask, QueryTriggerInteraction.Collide);
            if (kind == 1) return Physics.SphereCastNonAlloc(center, radius + margin, movement.normalized, output, movement.magnitude, mask, QueryTriggerInteraction.Collide);
            Vector3 end = rotation * axis * segment;
            return Physics.CapsuleCastNonAlloc(center - end, center + end, radius + margin, movement.normalized, output, movement.magnitude, mask, QueryTriggerInteraction.Collide);
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
        public string Prepare(PlayerControls chef, GameObject item, WindVolume[] discovered = null)
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
            foreach (WindVolume volume in discovered ?? UnityEngine.Object.FindObjectsOfType<WindVolume>())
            {
                if (!volume.isActiveAndEnabled || (ActionObservations.Read<LayerMask>(volume, "m_windFilter").value & (1 << ItemBody.DetachedLayer(item))) == 0) continue;
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
        public readonly List<Vector3> Velocities = new List<Vector3>();
        public Vector3 End, InitialVelocity, FinalVelocity, WindDisplacement;
        public float PeakHeight;
    }

    internal static class ThrowFlight
    {
        public static FlightPath Simulate(Vector3 origin, Vector3 direction, Vector3 launch, Vector3 gravity, float drag, float time, float step, WindField wind, FlightPath scratch = null)
        {
            // A planner runs many shooting iterations per candidate. Reuse only
            // its private scratch buffers; published ThrowPlans copy scalar data.
            var result = scratch ?? new FlightPath();
            result.Points.Clear(); result.Times.Clear(); result.Velocities.Clear();
            result.InitialVelocity = launch; result.PeakHeight = origin.y;
            result.End = result.FinalVelocity = result.WindDisplacement = Vector3.zero;
            Vector3 position = origin, velocity = launch; ulong membership = wind.At(position, direction);
            result.Points.Add(position); result.Times.Add(0); result.Velocities.Add(velocity);
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
                result.Points.Add(position); result.Times.Add(elapsed); result.Velocities.Add(velocity);
            }
            result.End = position; result.FinalVelocity = velocity; return result;
        }
    }
}
