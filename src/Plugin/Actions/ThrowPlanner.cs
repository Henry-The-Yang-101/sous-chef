using System;
using System.Collections.Generic;
using System.Diagnostics;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class ThrowPlan
    {
        public Vector3 Stance, Direction, Origin, Arrival;
        public float FlightSeconds, WalkDistance;
        public object Describe()
        {
            return Json.Object("releasePosition", Values.Vector(Stance), "direction", Values.Vector(Direction),
                "origin", Values.Vector(Origin), "predictedArrival", Values.Vector(Arrival),
                "flightSeconds", FlightSeconds, "walkDistance", WalkDistance,
                "model", "native_fixed_inclination_physx_damping", "validatedInGame", false);
        }
    }

    internal sealed class ThrowPlanner
    {
        private readonly RaycastHit[] hits = new RaycastHit[128];
        private readonly Collider[] overlaps = new Collider[128];
        private float speed, vertical, radius, pivot, drag;
        private Vector3 localRelease;
        private PlayerControls chef;
        private GameObject item, target, receiver;
        private PlayerControls player;
        private Bounds receiving;
        private int mask;

        public string Prepare(PlayerControls actor, GameObject held, GameObject destination)
        {
            chef = actor; item = held; target = destination;
            if (item == null) return "hands_empty";
            var throwable = GameObjectUtils.RequestInterface<IClientThrowable>(item);
            var thrower = chef.GetComponent<ClientAttachmentThrower>();
            var launch = chef.GetComponent<AttachmentThrower>();
            var physical = item.GetComponent<PhysicalAttachment>();
            if (throwable == null || thrower == null || launch == null || physical == null) return "item_not_throwable";
            var parameters = physical.m_rigidBodyParams;
            if (parameters == null || !parameters.m_useGravity || !ThrowTrajectory.Finite(parameters.m_drag) || parameters.m_drag < 0 ||
                Mathf.Abs(Physics.gravity.x) > .001f || Mathf.Abs(Physics.gravity.z) > .001f || Physics.gravity.y >= -.001f)
                return "unsupported_throw_physics";
            drag = parameters.m_drag;
            if (((int)parameters.m_constraints & 14) != 0) return "unsupported_throw_constraints";
            if (Time.fixedDeltaTime < .001f || Time.fixedDeltaTime > .1f) return "unsupported_throw_timestep";
            speed = launch.m_throwForce; vertical = speed * Mathf.Tan(launch.m_throwInclination * Mathf.Deg2Rad);
            if (!ThrowTrajectory.Finite(speed) || speed <= 0 || !ThrowTrajectory.Finite(vertical)) return "unsupported_throw_physics";
            var shape = item.GetComponent<Collider>();
            var capsule = chef.GetComponent<CapsuleCollider>();
            if (shape == null || capsule == null) return "unsupported_throw_geometry";
            // Held colliders may be disabled, in which case Unity returns empty
            // bounds. Read their actual shape and enclose corners as well as axes.
            Vector3 scale = shape.transform.lossyScale; scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var box = shape as BoxCollider; var sphere = shape as SphereCollider; var pill = shape as CapsuleCollider;
            Vector3 shapeCenter;
            if (box != null)
            {
                shapeCenter = box.center;
                radius = new Vector3(box.size.x * scale.x, box.size.y * scale.y, box.size.z * scale.z).magnitude * .5f;
            }
            else if (sphere != null) { shapeCenter = sphere.center; radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)); }
            else if (pill != null)
            {
                shapeCenter = pill.center;
                radius = Mathf.Max(pill.height * .5f, pill.radius) * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            }
            else return "unsupported_throw_geometry";
            radius = Mathf.Max(.05f, radius + Vector3.Distance(shape.transform.TransformPoint(shapeCenter), item.transform.position));
            if (radius > .6f) return "unsupported_throw_geometry";
            localRelease = chef.transform.InverseTransformPoint(item.transform.position);
            pivot = chef.transform.position.y - capsule.bounds.min.y;
            mask = 0;
            for (int layer = 0; layer < 32; layer++) if (!Physics.GetIgnoreLayerCollision(item.layer, layer)) mask |= 1 << layer;
            player = target.GetComponent<PlayerControls>(); receiver = ActionObservations.Receiver(target);
            if (player != null)
            {
                if (GameObjectUtils.RequestInterface<ICatchable>(item) == null) return "item_not_catchable";
                var configuration = PlayerInputLookup.GetInputConfig();
                var entry = configuration == null || player.PlayerIDProvider == null ? null : configuration.GetInputConfigEntry(player.PlayerIDProvider.GetID());
                if (entry == null || !entry.IsLocal()) return "player_not_local";
                if (player == chef || player.m_bRespawning || !player.GetDirectlyUnderPlayerControl() ||
                    player.GetComponent<AttachmentCatcher>() == null || ActionObservations.Held(player) != null) return "player_cannot_catch";
                var catching = player.GetComponent<AttachmentCatcher>();
                float distance = Mathf.Max(.1f, catching.m_catchDistance - .1f);
                receiving = new Bounds(player.transform.position, Vector3.one * (2 * distance));
            }
            else
            {
                var catcher = GameObjectUtils.RequestInterface<IHandleCatch>(receiver);
                var catchable = GameObjectUtils.RequestInterface<ICatchable>(item);
                if (catcher == null || catchable == null) return "target_cannot_catch";
                var colliders = receiver.GetComponentsInChildren<Collider>(); bool found = false;
                receiving = new Bounds(receiver.transform.position, Vector3.zero);
                foreach (Collider c in colliders)
                {
                    if (!c.enabled || !c.gameObject.activeInHierarchy || c.isTrigger) continue;
                    if (!found) { receiving = c.bounds; found = true; } else receiving.Encapsulate(c.bounds);
                }
                if (!found) return "unsupported_receiver_geometry";
                // Aim at the top of the actual collider, on the descending arc.
                Vector3 center = receiving.center; center.y = receiving.max.y + radius;
                receiving = new Bounds(center, new Vector3(Mathf.Max(.08f, receiving.size.x - radius * 2), .22f, Mathf.Max(.08f, receiving.size.z - radius * 2)));
            }
            return null;
        }
        private Vector3 ReceiverVelocity()
        {
            if (player != null)
            {
                Vector3 v = player.Motion.GetVelocity();
                if (player.WindReceiver != null) v += player.WindReceiver.GetVelocity();
                if (player.SurfaceMovable != null) v += player.SurfaceMovable.GetVelocity();
                return v;
            }
            var surface = target.GetComponent<SurfaceMovable>();
            return surface == null ? Vector3.zero : surface.GetVelocity();
        }
        public string Find(NavigationActions navigation, out ThrowPlan plan)
        {
            plan = null; navigation.PrepareStances(chef);
            Vector3 foot = chef.transform.position - Vector3.up * pivot;
            var candidates = new List<Vector3> { foot };
            float flight;
            if (!ThrowTrajectory.FlightTime(receiving.center.y - (chef.transform.position.y + localRelease.y),
                vertical, Physics.gravity.y, drag, Time.fixedDeltaTime, out flight)) return "no_valid_throw";
            float distance = ThrowTrajectory.Displacement(speed, 0, drag, flight, Time.fixedDeltaTime) + new Vector2(localRelease.x, localRelease.z).magnitude;
            Vector3 predictedCenter = receiving.center + ReceiverVelocity() * flight;
            // Invert the fixed launch arc into a release ring. Include nearby rings
            // to account for receiver volume, mount offset and differing floors.
            for (int i = 0; i < 32; i++) for (int ring = -1; ring <= 1; ring++)
            {
                float angle = i * 2 * Mathf.PI / 32, r = Mathf.Max(.5f, distance + ring * .15f);
                candidates.Add(new Vector3(predictedCenter.x + Mathf.Cos(angle) * r, foot.y, predictedCenter.z + Mathf.Sin(angle) * r));
            }
            float best = Single.MaxValue;
            var budget = Stopwatch.StartNew();
            foreach (Vector3 candidate in candidates)
            {
                if (budget.Elapsed.TotalMilliseconds > 25) break;
                Vector3 stance; ThrowPlan possible;
                if (!navigation.TryStance(candidate, out stance) || !From(stance, false, out possible)) continue;
                float cost = navigation.RouteCost(stance);
                if (cost == Single.MaxValue || cost >= best) continue;
                possible.WalkDistance = cost; best = cost; plan = possible;
            }
            return plan == null ? (budget.Elapsed.TotalMilliseconds > 25 ? "throw_planning_budget_exceeded" : "no_reachable_throw_position") : null;
        }
        public bool From(Vector3 stance, bool actualRelease, out ThrowPlan plan)
        {
            plan = null;
            if (actualRelease && chef.CurrentInteractionObjects.m_interactable != null && chef.CurrentInteractionObjects.m_interactable.CanInteract(chef.gameObject)) return false;
            Vector3 velocity = ReceiverVelocity(), destination = receiving.center;
            if (velocity.magnitude > 6 || Mathf.Abs(velocity.y) > .2f) return false;
            Vector3 origin = stance + Vector3.up * pivot, direction = destination - origin;
            direction.y = 0;
            if (direction.sqrMagnitude < .001f) return false;
            direction.Normalize(); float time = 0;
            for (int i = 0; i < 5; i++)
            {
                origin = actualRelease ? item.transform.position : stance + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
                if (!ThrowTrajectory.Intercept(destination.x - origin.x, destination.z - origin.z, velocity.x, velocity.z, speed, drag, Time.fixedDeltaTime, out time)) return false;
                direction = destination + velocity * time - origin; direction.y = 0; direction.Normalize();
            }
            if (actualRelease && Vector3.Dot(direction, chef.transform.forward) < Mathf.Cos(.75f * Mathf.Deg2Rad)) return false;
            Vector3 arrival = origin + direction * ThrowTrajectory.Displacement(speed, 0, drag, time, Time.fixedDeltaTime);
            arrival.y = origin.y + ThrowTrajectory.Displacement(vertical, Physics.gravity.y, drag, time, Time.fixedDeltaTime);
            Vector3 predicted = destination + velocity * time;
            if (player != null)
            {
                var catcher = player.GetComponent<AttachmentCatcher>();
                if (Vector3.Distance(arrival, predicted) > catcher.m_catchDistance - .15f ||
                    Vector3.Angle(direction, -player.transform.forward) > Mathf.Max(0, catcher.m_catchAngleMax - 5)) return false;
            }
            else
            {
                Bounds shifted = receiving; shifted.center = predicted;
                if (!shifted.Contains(arrival) || vertical + Physics.gravity.y * time >= 0) return false;
                var catcher = GameObjectUtils.RequestInterface<IHandleCatch>(receiver);
                var catchable = GameObjectUtils.RequestInterface<ICatchable>(item);
                if (catcher == null || catchable == null || !catcher.CanHandleCatch(catchable, new Vector2(direction.x, direction.z))) return false;
            }
            var throwable = GameObjectUtils.RequestInterface<IClientThrowable>(item);
            if (throwable == null || !throwable.CanHandleThrow(chef.GetComponent<ClientAttachmentThrower>(), new Vector2(direction.x, direction.z)) ||
                !Clear(origin, direction, time)) return false;
            plan = new ThrowPlan { Stance = stance, Origin = origin, Direction = direction, Arrival = arrival, FlightSeconds = time }; return true;
        }
        private bool Clear(Vector3 origin, Vector3 direction, float time)
        {
            int count = Physics.OverlapSphereNonAlloc(origin, radius + .02f, overlaps, mask, QueryTriggerInteraction.Collide);
            if (count >= overlaps.Length) return false;
            for (int i = 0; i < count; i++) if (Obstruction(overlaps[i], false)) return false;
            Vector3 previous = origin;
            int steps = Mathf.CeilToInt(time / Mathf.Min(.02f, .15f / speed));
            if (steps > 1000) return false;
            for (int step = 1; step <= steps; step++)
            {
                float t = time * step / steps;
                Vector3 next = origin + direction * ThrowTrajectory.Displacement(speed, 0, drag, t, Time.fixedDeltaTime);
                next.y = origin.y + ThrowTrajectory.Displacement(vertical, Physics.gravity.y, drag, t, Time.fixedDeltaTime);
                Vector3 segment = next - previous;
                int n = Physics.SphereCastNonAlloc(previous, radius + .015f, segment.normalized, hits, segment.magnitude, mask, QueryTriggerInteraction.Collide);
                if (n >= hits.Length) return false;
                bool receivingEnd = step >= steps - 2;
                if (player != null && t >= .1f)
                    receivingEnd |= Vector3.Distance(next, player.transform.position + ReceiverVelocity() * t) <= player.GetComponent<AttachmentCatcher>().m_catchDistance - .1f;
                for (int i = 0; i < n; i++) if (Obstruction(hits[i].collider, receivingEnd)) return false;
                // Environmental force fields cannot be represented by a gravity-only
                // arc. Reject them instead of silently predicting through wind.
                n = Physics.OverlapSphereNonAlloc(next, radius, overlaps, ~0, QueryTriggerInteraction.Collide);
                if (n >= overlaps.Length) return false;
                for (int i = 0; i < n; i++)
                {
                    var wind = overlaps[i] == null ? null : overlaps[i].GetComponent<WindVolume>();
                    if (wind != null && ActionObservations.Read<bool>(wind, "m_isWindy")) return false;
                }
                previous = next;
            }
            return true;
        }
        private bool Obstruction(Collider c, bool receivingEnd)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || c.transform.IsChildOf(chef.transform) || c.transform.IsChildOf(item.transform)) return false;
            if (ActionObservations.Matches(c.gameObject, target) || ActionObservations.Matches(c.gameObject, receiver)) return !receivingEnd;
            return !c.isTrigger;
        }
    }
}
