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
        public Vector3 Stance, Direction, Origin, Arrival, InitialVelocity, WindDisplacement;
        public float PeakHeight, Drag;
        public float FlightSeconds, WalkDistance;
        public object Describe()
        {
            return Json.Object("releasePosition", Values.Vector(Stance), "direction", Values.Vector(Direction),
                "origin", Values.Vector(Origin), "predictedArrival", Values.Vector(Arrival),
                "initialVelocity", Values.Vector(InitialVelocity), "launchSpeed", InitialVelocity.magnitude,
                "launchVelocitySource", "ServerAttachmentThrower.CalculateThrowVelocity",
                "windDisplacement", Values.Vector(WindDisplacement), "releaseHeight", Origin.y, "arrivalHeight", Arrival.y, "peakHeight", PeakHeight, "drag", Drag,
                "flightSeconds", FlightSeconds, "walkDistance", WalkDistance,
                "model", "native_launch_fixed_step_gravity_drag_wind_exit_velocity");
        }
    }

    internal sealed class ThrowPlanner
    {
        private readonly RaycastHit[] hits = new RaycastHit[128];
        private readonly Collider[] overlaps = new Collider[128];
        private float speed, vertical, pivot, drag;
        private readonly ItemBody itemBody = new ItemBody();
        private Bounds receiverBounds;
        private Vector3 localRelease;
        private PlayerControls chef;
        private GameObject item, target, receiver;
        private PlayerControls player;
        private Bounds receiving;
        private int mask;
        private readonly WindField wind = new WindField();
        private readonly Dictionary<string, object> rejected = new Dictionary<string, object>();
        public const string Pending = "throw_planning";
        public void CancelPlanning() { if (candidates != null) candidates.Dispose(); candidates = null; }
        private IEnumerator<Vector3> candidates;
        private Stopwatch planningTime;
        private ThrowPlan bestPlan;
        private Vector3 lastArrival;
        private string lastObstruction;
        public object Diagnostics { get { return Json.Object("rejected", rejected, "receivingCenter", Values.Vector(receiving.center), "receivingSize", Values.Vector(receiving.size), "lastArrival", Values.Vector(lastArrival), "lastObstruction", lastObstruction); } }
        private bool Reject(string reason) { object count; rejected.TryGetValue(reason, out count); rejected[reason] = (count == null ? 0 : (int)count) + 1; return false; }

        public string Prepare(PlayerControls actor, GameObject held, GameObject destination)
        {
            CancelPlanning(); bestPlan = null;
            rejected.Clear(); lastObstruction = null;
            chef = actor; item = held; target = destination;
            if (item == null) return "hands_empty";
            var throwable = GameObjectUtils.RequestInterface<IClientThrowable>(item);
            var thrower = chef.GetComponent<ClientAttachmentThrower>();
            var physical = item.GetComponent<PhysicalAttachment>();
            if (throwable == null || thrower == null || physical == null) return "item_not_throwable";
            var parameters = physical.m_rigidBodyParams;
            if (parameters == null || !parameters.m_useGravity || !ThrowTrajectory.Finite(parameters.m_drag) || parameters.m_drag < 0 ||
                !ThrowTrajectory.Finite(Physics.gravity.x) || !ThrowTrajectory.Finite(Physics.gravity.z) || Physics.gravity.y >= -.001f)
                return "unsupported_throw_physics";
            drag = parameters.m_drag;
            var body = physical.m_container;
            if (body != null)
            {
                if (!body.useGravity || !ThrowTrajectory.Finite(body.drag) || body.drag < 0 || ((int)body.constraints & 14) != 0) return "unsupported_throw_physics";
                drag = body.drag;
            }
            if (((int)parameters.m_constraints & 14) != 0) return "unsupported_throw_constraints";
            if (Time.fixedDeltaTime < .001f || Time.fixedDeltaTime > .1f) return "unsupported_throw_timestep";
            Vector3 nativeVelocity;
            if (!NativeThrow.Velocity(chef, Vector3.forward, out nativeVelocity)) return "native_launch_velocity_unavailable";
            speed = new Vector2(nativeVelocity.x, nativeVelocity.z).magnitude; vertical = nativeVelocity.y;
            if (!ThrowTrajectory.Finite(speed) || speed <= 0 || !ThrowTrajectory.Finite(vertical)) return "unsupported_throw_physics";
            string windError = wind.Prepare(chef, item); if (windError != null) return windError;
            var shape = item.GetComponent<Collider>();
            var capsule = chef.GetComponent<CapsuleCollider>();
            if (shape == null || capsule == null) return "unsupported_throw_geometry";
            // Held collider bounds may be empty; use actual scaled shape data.
            if (!itemBody.Prepare(chef, item)) return "unsupported_throw_geometry";
            localRelease = chef.transform.InverseTransformPoint(item.transform.position);
            pivot = chef.transform.position.y - capsule.bounds.min.y;
            mask = 0; int detachedLayer = ItemBody.DetachedLayer(item);
            for (int layer = 0; layer < 32; layer++) if (!Physics.GetIgnoreLayerCollision(detachedLayer, layer)) mask |= 1 << layer;
            player = target.GetComponent<PlayerControls>(); receiver = ActionObservations.Receiver(target);
            if (player != null)
            {
                if (GameObjectUtils.RequestInterface<ICatchable>(item) == null) return "item_not_catchable";
                var configuration = PlayerInputLookup.GetInputConfig();
                var entry = configuration == null || player.PlayerIDProvider == null ? null : configuration.GetInputConfigEntry(player.PlayerIDProvider.GetID());
                if (entry == null || !entry.IsLocal()) return "player_not_local";
                if (player == chef || player.m_bRespawning || !player.GetDirectlyUnderPlayerControl() ||
                    player.GetComponent<AttachmentCatcher>() == null || ActionObservations.Held(player) != null) return "player_cannot_catch";
                if ((ActionObservations.Read<LayerMask>(player, "m_catchMask").value & (1 << detachedLayer)) == 0) return "player_cannot_catch";
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
                    if (!c.enabled || !c.gameObject.activeInHierarchy || c.isTrigger || (mask & (1 << c.gameObject.layer)) == 0) continue;
                    if (!found) { receiving = c.bounds; found = true; } else receiving.Encapsulate(c.bounds);
                }
                if (!found) return "unsupported_receiver_geometry";
                receiverBounds = receiving;
                Vector3 toward = receiver.transform.position - chef.transform.position; toward.y = 0;
                receiving = itemBody.Receiving(receiverBounds, toward.sqrMagnitude > .001f ? toward.normalized : chef.transform.forward);
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
            plan = null;
            if (candidates == null)
            {
                navigation.PrepareStances(chef);
                candidates = CandidatePositions(navigation).GetEnumerator();
                planningTime = Stopwatch.StartNew(); bestPlan = null;
            }
            var frameBudget = Stopwatch.StartNew();
            // Continue finite candidate work on later Updates instead of calling
            // an incomplete/cold search proof that no release position exists.
            while (frameBudget.Elapsed.TotalMilliseconds < 25)
            {
                if (!candidates.MoveNext()) return CompletePlanning(out plan);
                Vector3 stance; ThrowPlan possible;
                if (!navigation.TryStance(candidates.Current, out stance)) { Reject("unsupported_stance"); continue; }
                if (!From(stance, false, out possible)) continue;
                float cost = navigation.RouteCost(stance);
                if (cost == Single.MaxValue) { Reject("unreachable_stance"); continue; }
                if (bestPlan != null && cost >= bestPlan.WalkDistance) continue;
                possible.WalkDistance = cost; bestPlan = possible;
                if (cost < .01f) return CompletePlanning(out plan);
            }
            if (planningTime.Elapsed.TotalSeconds >= 2)
            {
                string result = CompletePlanning(out plan);
                return result == null ? null : "throw_planning_budget_exceeded";
            }
            return Pending;
        }
        private string CompletePlanning(out ThrowPlan plan)
        {
            candidates.Dispose(); candidates = null;
            plan = bestPlan; return plan == null ? "no_reachable_throw_position" : null;
        }
        private IEnumerable<Vector3> CandidatePositions(NavigationActions navigation)
        {
            Vector3 center = receiving.center;
            yield return chef.transform.position - Vector3.up * pivot;
            // Invert the native launch arc into a release ring. Three offsets
            // account for the finite receiving volume without changing force.
            foreach (float height in navigation.ReleaseHeights(center, speed * 4))
            {
                float flight;
                if (!ThrowTrajectory.FlightTime(center.y - (height + pivot + localRelease.y), vertical, Physics.gravity.y, drag, Time.fixedDeltaTime, out flight))
                { if (!wind.HasWind) continue; flight = .5f; }
                float distance = ThrowTrajectory.Displacement(speed, 0, drag, flight, Time.fixedDeltaTime) + new Vector2(localRelease.x, localRelease.z).magnitude;
                Vector3 predictedCenter = center + ReceiverVelocity() * flight;
                for (int i = 0; i < 24; i++)
                {
                    float angle = i * 2 * Mathf.PI / 24, r = Mathf.Max(.5f, distance);
                    Vector3 radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                    Vector3 candidate = new Vector3(predictedCenter.x + radial.x * r, height, predictedCenter.z + radial.z * r);
                    Vector3 direction = predictedCenter - candidate; direction.y = 0; direction.Normalize(); Vector3 launch;
                    if (!NativeThrow.Velocity(chef, direction, out launch)) continue;
                    Vector3 origin = candidate + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
                    Vector3 end; float seedTime;
                    if (!Seed(origin, direction, launch, flight, out seedTime, out end)) continue;
                    Vector3 correction = center + ReceiverVelocity() * seedTime - end; correction.y = 0;
                    for (int ring = -1; ring <= 1; ring++) yield return candidate + correction + radial * (ring * .25f);
                }
            }
        }
        private bool Seed(Vector3 origin, Vector3 direction, Vector3 launch, float calmTime, out float time, out Vector3 end)
        {
            time = calmTime; end = Vector3.zero;
            if (!wind.HasWind)
            { end = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, calmTime, Time.fixedDeltaTime, wind).End; return true; }
            var path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, 4, Time.fixedDeltaTime, wind);
            Vector3 targetVelocity = ReceiverVelocity();
            for (int i = 1; i < path.Points.Count; i++)
            {
                if (path.Times[i] < .1f) continue;
                float previous = path.Points[i - 1].y - receiving.center.y - targetVelocity.y * path.Times[i - 1];
                float next = path.Points[i].y - receiving.center.y - targetVelocity.y * path.Times[i];
                if (previous >= 0 && next <= 0 && next < previous)
                {
                    float fraction = previous / (previous - next);
                    time = Mathf.Lerp(path.Times[i - 1], path.Times[i], fraction);
                    end = Vector3.Lerp(path.Points[i - 1], path.Points[i], fraction); return true;
                }
            }
            return false;
        }
        public bool From(Vector3 stance, bool actualRelease, out ThrowPlan plan)
        {
            plan = null;
            if (actualRelease && chef.CurrentInteractionObjects.m_interactable != null && chef.CurrentInteractionObjects.m_interactable.CanInteract(chef.gameObject)) return Reject("interactable_selected");
            Vector3 velocity = ReceiverVelocity(), destination = receiving.center;
            if (velocity.magnitude > Mathf.Max(20, speed * 2)) return Reject("receiver_velocity");
            Vector3 origin = stance + Vector3.up * pivot, direction = destination - origin;
            direction.y = 0;
            if (direction.sqrMagnitude < .001f) return Reject("coincident_target");
            direction.Normalize(); float time = 0; Vector3 correction = Vector3.zero, launch = Vector3.zero; FlightPath path = null;
            // Shooting iterations solve native horizontal launch plus the sampled
            // spatial wind field. Wind exit impulses are part of the simulation,
            // not a single constant acceleration pasted onto a ballistic arc.
            for (int i = 0; i < 8; i++)
            {
                if (player == null) { receiving = itemBody.Receiving(receiverBounds, direction); destination = receiving.center; }
                origin = actualRelease ? item.transform.position : stance + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
                if (player != null)
                {
                    if (!ThrowTrajectory.Intercept(destination.x - origin.x - correction.x, destination.z - origin.z - correction.z,
                        velocity.x, velocity.z, speed, drag, Time.fixedDeltaTime, out time)) return Reject("horizontal_intercept");
                }
                else
                {
                    float calmTime;
                    if (!ThrowTrajectory.FlightTime(destination.y - origin.y, vertical, Physics.gravity.y, drag, Time.fixedDeltaTime, out calmTime))
                    { if (!wind.HasWind) return Reject("receiving_height"); calmTime = .5f; }
                    if (!NativeThrow.Velocity(chef, direction, out launch) || !Seed(origin, direction, launch, calmTime, out time, out lastArrival)) return Reject("receiving_height");
                }
                direction = destination + velocity * time - origin - correction; direction.y = 0; direction.Normalize();
                if (!NativeThrow.Velocity(chef, direction, out launch)) return Reject("native_launch_velocity");
                path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, time, Time.fixedDeltaTime, wind);
                correction = path.End - origin - direction * ThrowTrajectory.Displacement(speed, 0, drag, time, Time.fixedDeltaTime); correction.y = 0;
            }
            if (actualRelease)
            {
                Vector3 nativeDirection = chef.transform.forward; nativeDirection.y = 0; nativeDirection.Normalize();
                if (Vector3.Dot(direction, nativeDirection) < Mathf.Cos(.75f * Mathf.Deg2Rad)) return Reject("release_alignment");
                direction = nativeDirection;
            }
            origin = actualRelease ? item.transform.position : stance + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
            if (!NativeThrow.Velocity(chef, direction, out launch)) return Reject("native_launch_velocity");
            path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, time, Time.fixedDeltaTime, wind);
            Vector3 arrival = path.End, predicted = destination + velocity * time; lastArrival = arrival;
            int lastStep = path.Points.Count - 1;
            if (player != null)
            {
                lastStep = -1;
                for (int i = 1; i < path.Points.Count; i++)
                    if (PlayerCatchable(path, i, direction, velocity, .1f)) { lastStep = i; break; }
                if (lastStep < 0) return Reject("player_catch_window");
                time = path.Times[lastStep]; arrival = path.Points[lastStep];
            }
            else
            {
                Bounds shifted = receiving; shifted.center = predicted;
                if (!shifted.Contains(arrival)) return Reject("receiving_volume");
                if (path.Points[path.Points.Count - 1].y >= path.Points[path.Points.Count - 2].y) return Reject("ascending_arrival");
                var catcher = GameObjectUtils.RequestInterface<IHandleCatch>(receiver);
                var catchable = GameObjectUtils.RequestInterface<ICatchable>(item);
                if (catcher == null || catchable == null || !catcher.CanHandleCatch(catchable, new Vector2(direction.x, direction.z))) return Reject("native_catch_veto");
            }
            var throwable = GameObjectUtils.RequestInterface<IClientThrowable>(item);
            if (throwable == null || !throwable.CanHandleThrow(chef.GetComponent<ClientAttachmentThrower>(), new Vector2(direction.x, direction.z))) return Reject("native_throw_veto");
            if (!Clear(path, velocity, direction, lastStep)) return Reject("obstructed");
            Vector3 calm = origin + new Vector3(ThrowTrajectory.Displacement(launch.x, Physics.gravity.x, drag, time, Time.fixedDeltaTime),
                ThrowTrajectory.Displacement(launch.y, Physics.gravity.y, drag, time, Time.fixedDeltaTime), ThrowTrajectory.Displacement(launch.z, Physics.gravity.z, drag, time, Time.fixedDeltaTime));
            plan = new ThrowPlan { Stance = stance, Origin = origin, Direction = direction, Arrival = arrival, FlightSeconds = time,
                InitialVelocity = launch, WindDisplacement = arrival - calm, PeakHeight = path.PeakHeight, Drag = drag }; return true;
        }
        private bool PlayerCatchable(FlightPath path, int index, Vector3 direction, Vector3 receiverVelocity, float margin)
        {
            float time = path.Times[index]; if (time < .1f + margin * .2f) return false;
            Vector3 point = path.Points[index], receiverPosition = player.transform.position + receiverVelocity * time;
            var catcher = player.GetComponent<AttachmentCatcher>();
            if (Vector3.Distance(point, receiverPosition) > catcher.m_catchDistance - margin) return false;
            Vector3 facing = player.transform.forward; facing.y = 0; facing.Normalize();
            Vector3 incoming = path.Velocities[index]; incoming.y = 0;
            if (Vector3.Angle(incoming, -facing) > Mathf.Max(0, catcher.m_catchAngleMax - margin * 50)) return false;
            Vector3 scan = itemBody.CatchScanPoint(point, direction, receiverPosition) - receiverPosition; scan.y = 0;
            // PlayerControls.ScanForCatch uses range 2 and a PI/2 total arc;
            // its closest-point scan is narrower than AttachmentCatcher's cone.
            return scan.sqrMagnitude > .001f && scan.sqrMagnitude < 4 && Vector3.Dot(facing, scan.normalized) >= Mathf.Cos((45 - margin * 30) * Mathf.Deg2Rad);
        }
        private bool Clear(FlightPath path, Vector3 receiverVelocity, Vector3 direction, int lastStep)
        {
            int count = itemBody.Overlap(path.Points[0], direction, overlaps, mask, .01f);
            if (count >= overlaps.Length) return false;
            for (int i = 0; i < count; i++) if (Obstruction(overlaps[i], false)) return false;
            for (int step = 1; step <= lastStep; step++)
            {
                Vector3 previous = path.Points[step - 1], next = path.Points[step], segment = next - previous;
                int n = itemBody.Cast(previous, direction, segment, hits, mask, .01f);
                if (n >= hits.Length) return false;
                bool receivingEnd = player == null ? step >= lastStep - 3 : PlayerCatchable(path, step, direction, receiverVelocity, 0);
                for (int i = 0; i < n; i++) if (Obstruction(hits[i].collider, receivingEnd)) return false;
            }
            return true;
        }
        private bool Obstruction(Collider c, bool receivingEnd)
        {
            if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy || c.transform.IsChildOf(chef.transform) || c.transform.IsChildOf(item.transform)) return false;
            if (receivingEnd && (ActionObservations.Matches(c.gameObject, target) || ActionObservations.Matches(c.gameObject, receiver))) return false;
            lastObstruction = c.gameObject.name; return true;
        }
    }
}
