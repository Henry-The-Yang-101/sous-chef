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
        // Seed solving nests inside aiming, so these must not share a buffer.
        private readonly FlightPath seedFlight = new FlightPath(), aimedFlight = new FlightPath();
        private readonly Dictionary<string, object> rejected = new Dictionary<string, object>();
        public const string Pending = "throw_planning";
        public void CancelPlanning() { if (candidates != null) candidates.Dispose(); candidates = null; }
        private IEnumerator<Vector3> candidates;
        private double planningMilliseconds;
        private ThrowPlan bestPlan;
        private Vector3 lastArrival;
        private string lastObstruction;
        public int AimIterations { get; private set; }
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
        private bool offloadCandidates;
        public string Find(NavigationActions navigation, out ThrowPlan plan, float sliceMilliseconds = 25, bool firstFeasible = false, bool prepareNavigation = true)
        {
            plan = null;
            if (candidates == null)
            {
                if (prepareNavigation) navigation.PrepareStances(chef);
                offloadCandidates = firstFeasible;
                candidates = CandidatePositions(navigation).GetEnumerator();
                planningMilliseconds = 0; bestPlan = null;
            }
            var frameBudget = Stopwatch.StartNew();
            // Continue finite candidate work on later Updates instead of calling
            // an incomplete/cold search proof that no release position exists.
            while (frameBudget.Elapsed.TotalMilliseconds < sliceMilliseconds)
            {
                if (!candidates.MoveNext()) return CompletePlanning(out plan);
                // A pending worker batch yields without consuming a candidate.
                if (Single.IsNaN(candidates.Current.x)) return Pending;
                Vector3 stance; ThrowPlan possible;
                if (!navigation.TryStance(candidates.Current, out stance)) { Reject("unsupported_stance"); continue; }
                float uncertainty = Vector3.Distance(stance, chef.transform.position - Vector3.up * pivot) < .02f ? 0 : ArrivalCheck.PositionTolerance + .02f;
                if (!From(stance, false, out possible, uncertainty)) continue;
                float cost = navigation.RouteCost(stance);
                if (cost == Single.MaxValue) { Reject("unreachable_stance"); continue; }
                if (bestPlan != null && cost >= bestPlan.WalkDistance) continue;
                possible.WalkDistance = cost; bestPlan = possible;
                if (firstFeasible || cost < .01f) return CompletePlanning(out plan);
            }
            // Count work, rather than time between Updates: read-only option
            // previews use shorter slices and must not expire while waiting.
            planningMilliseconds += frameBudget.Elapsed.TotalMilliseconds;
            if (planningMilliseconds >= 2000)
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
        private IEnumerable<float> ReleaseTimes(float height, Vector3 center)
        {
            float flight;
            if (ThrowTrajectory.FlightTime(center.y - (height + pivot + localRelease.y), vertical, Physics.gravity.y, drag, Time.fixedDeltaTime, out flight)) yield return flight;
            else if (wind.HasWind) yield return .5f;
            if (player != null)
            {
                // A chef catches inside a volume before the projectile reaches
                // their feet. Include earlier native flight positions, rather
                // than limiting passes to the descending ground-height ring.
                yield return .35f; yield return .5f; yield return .2f;
            }
        }
        private IEnumerable<Vector3> CandidatePositions(NavigationActions navigation)
        {
            Vector3 center = receiving.center;
            yield return chef.transform.position - Vector3.up * pivot;
            foreach (float height in navigation.ReleaseHeights(center, speed * 4))
                foreach (float flight in ReleaseTimes(height, center))
                {
                    float distance = ThrowTrajectory.Displacement(speed, 0, drag, flight, Time.fixedDeltaTime) + new Vector2(localRelease.x, localRelease.z).magnitude;
                    Vector3 predictedCenter = center + ReceiverVelocity() * flight;
                    const int angles = 36;
                    // Batch all calm seed endpoints in one IPC job. Native launch
                    // parameters are sampled here; From still performs full fresh
                    // aiming, flight, collision and receiver validation for every
                    // resulting stance. Wind retains positional native queries.
                    Vector3[] remoteEnds = null; float[][] remoteInputs = null;
                    WorkerHost worker = WorkerHost.Current;
                    if (offloadCandidates && !wind.HasWind && worker != null && worker.Ready && worker.Flights != null)
                    {
                        var inputs = new float[angles][]; bool valid = true;
                        for (int a = 0; a < angles; a++)
                        {
                            float angle = a * 2 * Mathf.PI / angles, r = Mathf.Max(.5f, distance);
                            Vector3 candidate = new Vector3(predictedCenter.x + Mathf.Cos(angle) * r, height, predictedCenter.z + Mathf.Sin(angle) * r);
                            Vector3 direction = predictedCenter - candidate; direction.y = 0; direction.Normalize(); Vector3 launch;
                            if (!NativeThrow.Velocity(chef, direction, out launch)) { valid = false; break; }
                            Vector3 origin = candidate + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
                            Vector3 gravity = Physics.gravity;
                            inputs[a] = new[] { origin.x, origin.y, origin.z, launch.x, launch.y, launch.z, gravity.x, gravity.y, gravity.z, drag, flight, Time.fixedDeltaTime };
                        }
                        FlightTask task = valid ? worker.Flights.Submit(inputs) : null;
                        if (task != null)
                        {
                            while (!task.Complete) yield return new Vector3(Single.NaN, 0, 0);
                            if (!task.Failed)
                            {
                                remoteEnds = new Vector3[angles]; remoteInputs = inputs;
                                for (int a = 0; a < angles; a++) remoteEnds[a] = new Vector3(task.Ends[a * 3], task.Ends[a * 3 + 1], task.Ends[a * 3 + 2]);
                            }
                        }
                    }
                    for (int i = 0; i < angles; i++)
                    {
                        float angle = i * 2 * Mathf.PI / angles, r = Mathf.Max(.5f, distance);
                        Vector3 radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                        Vector3 candidate = new Vector3(predictedCenter.x + radial.x * r, height, predictedCenter.z + radial.z * r);
                        Vector3 direction = predictedCenter - candidate; direction.y = 0; direction.Normalize(); Vector3 launch;
                        if (!NativeThrow.Velocity(chef, direction, out launch)) continue;
                        Vector3 origin = candidate + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
                        Vector3 end; float seedTime;
                        float[] sampled = remoteInputs == null ? null : remoteInputs[i];
                        Vector3 gravityNow = Physics.gravity;
                        bool sameInputs = sampled != null && sampled[0] == origin.x && sampled[1] == origin.y && sampled[2] == origin.z &&
                            sampled[3] == launch.x && sampled[4] == launch.y && sampled[5] == launch.z &&
                            sampled[6] == gravityNow.x && sampled[7] == gravityNow.y && sampled[8] == gravityNow.z &&
                            sampled[9] == drag && sampled[10] == flight && sampled[11] == Time.fixedDeltaTime;
                        if (sameInputs) { seedTime = flight; end = remoteEnds[i]; }
                        else if (player != null)
                        {
                            seedTime = flight;
                            end = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, flight, Time.fixedDeltaTime, wind, seedFlight).End;
                        }
                        else if (!Seed(origin, direction, launch, flight, out seedTime, out end)) continue;
                        Vector3 correction = center + ReceiverVelocity() * seedTime - end; correction.y = 0;
                        for (int ring = -1; ring <= 1; ring++)
                        {
                            Vector3 stance = candidate + correction + radial * (ring * .25f);
                            yield return stance;
                            if (player == null)
                            {
                                // Narrow release corridors can fall between
                                // angular samples. Search their lateral room as
                                // well as distance from the receiving surface.
                                Vector3 lateral = new Vector3(-radial.z, 0, radial.x) * .2f;
                                yield return stance + lateral; yield return stance - lateral;
                            }
                        }
                    }
                }
        }
        private bool Seed(Vector3 origin, Vector3 direction, Vector3 launch, float calmTime, out float time, out Vector3 end)
        {
            time = calmTime; end = Vector3.zero;
            if (!wind.HasWind)
            { end = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, calmTime, Time.fixedDeltaTime, wind, seedFlight).End; return true; }
            var path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, 4, Time.fixedDeltaTime, wind, seedFlight);
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
        public bool From(Vector3 stance, bool actualRelease, out ThrowPlan plan, float stanceUncertainty = 0)
        {
            AimIterations = 0;
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
                Vector3 previousDirection = direction, previousCorrection = correction;
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
                path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, time, Time.fixedDeltaTime, wind, aimedFlight);
                correction = path.End - origin - direction * ThrowTrajectory.Displacement(speed, 0, drag, time, Time.fixedDeltaTime); correction.y = 0;
                AimIterations++;
                // Exact fixed point, not an approximation tolerance: the next
                // shooting iteration would have identical inputs and output.
                // The full final flight, collision sweeps and native vetoes
                // below still run even when aim converges early.
                if (direction.Equals(previousDirection) && correction.Equals(previousCorrection)) break;
            }
            if (actualRelease)
            {
                Vector3 nativeDirection = chef.transform.forward; nativeDirection.y = 0; nativeDirection.Normalize();
                if (Vector3.Dot(direction, nativeDirection) < Mathf.Cos(.75f * Mathf.Deg2Rad)) return Reject("release_alignment");
                direction = nativeDirection;
            }
            origin = actualRelease ? item.transform.position : stance + Vector3.up * pivot + Quaternion.LookRotation(direction) * localRelease;
            if (!NativeThrow.Velocity(chef, direction, out launch)) return Reject("native_launch_velocity");
            path = ThrowFlight.Simulate(origin, direction, launch, Physics.gravity, drag, time, Time.fixedDeltaTime, wind, aimedFlight);
            Vector3 arrival = path.End, predicted = destination + velocity * time; lastArrival = arrival;
            int lastStep = path.Points.Count - 1;
            if (player != null)
            {
                lastStep = -1;
                for (int i = 1; i < path.Points.Count; i++)
                    if (PlayerCatchable(path, i, direction, velocity, .1f + stanceUncertainty)) { lastStep = i; break; }
                if (lastStep < 0) return Reject("player_catch_window");
                time = path.Times[lastStep]; arrival = path.Points[lastStep];
            }
            else
            {
                Bounds shifted = receiving; shifted.center = predicted;
                if (!shifted.Contains(arrival)) return Reject("receiving_volume");
                Vector3 rangeError = PoseUncertainty(direction, 1, stanceUncertainty);
                if (!shifted.Contains(arrival + rangeError) || !shifted.Contains(arrival - rangeError)) return Reject("receiving_range_uncertainty");
                if (path.Points[path.Points.Count - 1].y >= path.Points[path.Points.Count - 2].y) return Reject("ascending_arrival");
                var catcher = GameObjectUtils.RequestInterface<IHandleCatch>(receiver);
                var catchable = GameObjectUtils.RequestInterface<ICatchable>(item);
                if (catcher == null || catchable == null || !catcher.CanHandleCatch(catchable, new Vector2(direction.x, direction.z))) return Reject("native_catch_veto");
            }
            var throwable = GameObjectUtils.RequestInterface<IClientThrowable>(item);
            if (throwable == null || !throwable.CanHandleThrow(chef.GetComponent<ClientAttachmentThrower>(), new Vector2(direction.x, direction.z))) return Reject("native_throw_veto");
            if (!Clear(path, velocity, direction, lastStep, stanceUncertainty)) return Reject("obstructed");
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
        internal static Vector3 PoseUncertainty(Vector3 direction, float progress, float amount)
        {
            // Aim corrects lateral displacement toward the receiver, but fixed
            // launch force cannot correct range error along the flight axis.
            return new Vector3(amount * Mathf.Lerp(1, Mathf.Abs(direction.x), progress), 0,
                amount * Mathf.Lerp(1, Mathf.Abs(direction.z), progress));
        }
        private bool Clear(FlightPath path, Vector3 receiverVelocity, Vector3 direction, int lastStep, float stanceUncertainty)
        {
            int count = itemBody.Overlap(path.Points[0], direction, overlaps, mask, .01f, stanceUncertainty);
            if (count >= overlaps.Length) return false;
            for (int i = 0; i < count; i++) if (Obstruction(overlaps[i], false)) return false;
            for (int step = 1; step <= lastStep; step++)
            {
                Vector3 previous = path.Points[step - 1], next = path.Points[step], segment = next - previous;
                // Navigation can settle anywhere inside its arrival tolerance.
                // Cover that release-pose uncertainty while choosing a stance;
                // lateral error tapers, while radial range uncertainty remains.
                Vector3 uncertainty = PoseUncertainty(direction, path.Times[step - 1] / path.Times[lastStep], stanceUncertainty);
                int n = itemBody.Cast(previous, direction, segment, hits, mask, .01f, uncertainty);
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
