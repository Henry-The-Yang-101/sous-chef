using System;
using System.Collections.Generic;
using System.Diagnostics;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        private sealed class Destination
        {
            public string Id, Kind;
            public GameObject Object;
            public Backpack Pack;
            public readonly List<Collider> Colliders = new List<Collider>();
            public WalkCell Goal;
            public Vector3 Approach, LocalApproach;
            public float Distance;
            public bool IsPoint, StrictPointArrival, MovementOnly;
            public Transform PointAnchor;
            public Collider PointSupport;
        }
        private readonly WalkPhysics physics = new WalkPhysics();
        public readonly ChefActionState Action = new ChefActionState();
        private readonly Dictionary<string, Destination> available = new Dictionary<string, Destination>();
        private readonly Dictionary<int, Destination> identities = new Dictionary<int, Destination>();
        private readonly Dictionary<long, List<WalkCell>> reachableByXZ = new Dictionary<long, List<WalkCell>>();
        private readonly List<WalkCell> reachablePortals = new List<WalkCell>();
        private readonly Action<Vector3, int> move;
        private readonly Action neutral;
        private WalkTree search;
        private RemoteWalk remoteSearch;
        private void ClearRemote() { if (remoteSearch != null) remoteSearch.Dispose(); remoteSearch = null; }
        private bool incrementalSearch, fastWarm;
        private int lastPlanFrame = -1, searchSlices, savedStartCount;
        private long searchGeneration;
        private bool searchReused;
        private List<Vector3> route;
        private Destination target;
        private Destination recoveryTarget;
        private object lastDestination;
        private object arrivalDetails;
        private readonly ArrivalCheck arrival = new ArrivalCheck();
        private int nextId, waypoint, warmTicks;
        private PlayerControls chef;
        private float lastPlan = -1, started, facingSince = -1;
        private Vector3 motionPosition, motionLocalPosition, motionParentPosition, targetVelocity, parentVelocity;
        private Transform motionParent;
        private float motionTime, observedRelativeSpeed;
        private bool haveMotion, motionSampleValid;
        private long revision;
        private string unavailable = "not_ready";
        private double planMilliseconds, prepareMilliseconds, searchMilliseconds, matchMilliseconds;
        private int nodeCount, candidateCount, startCount;
        private string observationError;
        private bool planning, focusedPlan, fullCatalogueRequested;
        private StationMatch searchFocus;
        private const double PlanningBudgetMilliseconds = 12;
        private const double RecoveryBudgetMilliseconds = 25;
        private const double IncrementalBudgetMilliseconds = 3;
        private float settlingSince = -1;
        private bool followingSupport;
        private readonly Queue<object> executionTrace = new Queue<object>();
        private void Trace(string phase, Vector3 goal, Vector3 input)
        {
            if (!physics.TraceEnabled || chef == null) return;
            var ground = chef.GetComponent<GroundCast>();
            var points = new List<object>();
            if (route != null) for (int i = waypoint; i < Math.Min(route.Count, waypoint + 5); i++) points.Add(Values.Vector(route[i]));
            executionTrace.Enqueue(Json.Object("time", Time.realtimeSinceStartup, "frame", Time.frameCount,
                "phase", phase, "position", Values.Vector(physics.Position), "velocity", Values.Vector(chef.Motion.GetVelocity()),
                "surfaceVelocity", chef.SurfaceMovable == null ? null : Values.Vector(chef.SurfaceMovable.GetVelocity()),
                "windVelocity", chef.WindReceiver == null ? null : Values.Vector(chef.WindReceiver.GetVelocity()),
                "groundId", chef.GroundCollider == null ? 0 : chef.GroundCollider.GetInstanceID(),
                "groundPoint", ground == null ? null : Values.Vector(ground.GetGroundPoint()),
                "groundNormal", ground == null ? null : Values.Vector(ground.GetGroundNormal()),
                "groundDistance", ground == null ? 0 : ground.GetGroundDistance(),
                "goal", Values.Vector(goal), "input", Values.Vector(input), "route", points,
                "settlement", physics.Settlement, "failure", physics.Failure, "startFailures", phase == "no_safe_start" ? physics.StartFailures : null));
            while (executionTrace.Count > 96) executionTrace.Dequeue();
        }
        public NavigationActions(Action<Vector3, int> walk, Action halt)
        {
            move = delegate(Vector3 input, int milliseconds)
            {
                if (input.sqrMagnitude < .0001f) ResetWalkingProgress();
                walk(input, milliseconds);
            };
            neutral = halt;
            physics.TraceEnabled = Environment.GetEnvironmentVariable("SOUS_CHEF_NAV_TRACE") == "1";
        }
        public GameObject Resolve(string id)
        {
            foreach (Destination d in identities.Values) if (d.Id == id && d.Object != null && d.Object.activeInHierarchy) return d.Object;
            return null;
        }
        private float nextSemanticScan;
        // Register observed identities without refreshing physics or mutating any
        // active route/destination. This also works on the first paused poll.
        public void ObserveTargets(PlayerControls player)
        {
            if (Time.realtimeSinceStartup < nextSemanticScan) return;
            nextSemanticScan = Time.realtimeSinceStartup + 1;
            foreach (Collider collider in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Transform t = collider.transform;
                for (int depth = 0; t != null && depth < 5; depth++, t = t.parent)
                {
                    string kind;
                    if (!Eligible(t.gameObject, player, out kind, true)) continue;
                    bool known = identities.ContainsKey(t.gameObject.GetInstanceID());
                    RegisterItem(t.gameObject);
                    if (!known) identities[t.gameObject.GetInstanceID()].Kind = kind;
                    break;
                }
            }
        }
        public List<GameObject> Targets()
        {
            var result = new List<GameObject>();
            foreach (Destination d in identities.Values) if (d.Object != null && d.Object.activeInHierarchy) result.Add(d.Object);
            return result;
        }
        public object TargetIdentity(GameObject go)
        {
            Destination d;
            return go != null && identities.TryGetValue(go.GetInstanceID(), out d) && d.Object == go ? Identity(d) : null;
        }
        public bool AligningAt(GameObject station)
        { return Action.State == "navigating" && target != null && !target.IsPoint && target.Object == station && facingSince >= 0; }
        public string RegisterItem(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return null;
            Destination d;
            if (!identities.TryGetValue(go.GetInstanceID(), out d) || !System.Object.ReferenceEquals(d.Object, go))
            { d = new Destination { Id = "d" + (++nextId), Object = go, Kind = "item" }; identities[go.GetInstanceID()] = d; }
            return d.Id;
        }
        public string StartPosition(PlayerControls player, int owner, GameObject aim, Vector3 position, bool strictArrival = false)
        {
            if (Action.State != "idle") return "action_in_progress";
            chef = player;
            string error = physics.Refresh(player); if (error != null) return error;
            Vector3 supported; Collider ground;
            bool valid = strictArrival ? physics.StationaryStand(position, out supported, out ground) : physics.Stand(position, out supported, out ground);
            if (!valid || Vector3.Distance(supported, position) > .04f) return strictArrival ? "unsafe_release_stance" : "destination_unreachable";
            var chosen = new Destination { Id = "throw_stance", Object = aim, Kind = "throw_stance", IsPoint = true, StrictPointArrival = strictArrival,
                Approach = supported, PointSupport = ground, LocalApproach = ground.transform.InverseTransformPoint(supported) };
            if (!Action.Start(owner, chosen.Id, true)) return "action_in_progress";
            settlingSince = -1; teleportsCompleted = 0; target = chosen; started = Time.realtimeSinceStartup;
            ResetWalkingProgress(); stallReroutes = 0;
            arrivalDetails = null; arrival.Reset(); facingSince = -1; haveMotion = motionSampleValid = false;
            Refresh(player, true, true);
            if (!ApplyPlan()) return Action.Reason;
            return null;
        }
        public void PrepareStances(PlayerControls player) { Refresh(player, true); }
        public bool SharesCurrentSupport(GameObject target)
        { return target != null && physics.SharesCurrentSupport(target.transform.position); }
        public bool TryStance(Vector3 point, out Vector3 stance)
        { Collider ground; return physics.StationaryStand(point, out stance, out ground); }
        public bool TryStance(Vector3 point, out Vector3 stance, out Collider support)
        { return physics.StationaryStand(point, out stance, out support); }
        public bool CanApproachLocally(GameObject go) { return LocalApproachDistance(go, Single.MaxValue) != Single.MaxValue; }
        public float LocalApproachDistance(GameObject go, float maximum)
        {
            Destination d;
            if (go == null || !identities.TryGetValue(go.GetInstanceID(), out d)) return Single.MaxValue;
            foreach (Collider c in d.Colliders)
            {
                for (int side = 0; side < 8; side++)
                {
                    float angle = side * Mathf.PI / 4;
                    Vector3 seed = c.bounds.center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 1.2f;
                    seed.y = physics.Position.y; Vector3 stance;
                    if (!physics.Approach(seed, c, out stance)) continue;
                    float direct = Vector3.Distance(physics.Position, stance);
                    if (direct > maximum) continue;
                    float cost = RouteCost(stance);
                    if (cost < Single.MaxValue && cost <= maximum) return cost;
                    if (physics.Segment(physics.Position, stance)) return direct;
                }
            }
            return Single.MaxValue;
        }
        public bool CanReachFloor(Vector3 floor)
        {
            Vector3 stance;
            return TryStance(floor, out stance) && Vector3.Distance(stance, floor) <= .12f && CanWalkTo(stance);
        }
        private bool CanWalkTo(Vector3 stance)
        {
            // A direct checked walk also works when a moving raft repeatedly
            // invalidates the background route tree.
            return RouteCost(stance) != Single.MaxValue || physics.Segment(physics.Position, stance);
        }
        public float RouteCost(Vector3 stance)
        {
            float best = Single.MaxValue;
            if (Vector3.Distance(physics.Position, stance) < .01f && physics.Segment(physics.Position, stance)) return 0;
            if (search == null) return best;
            // A connector can only reach nodes within two lattice spacings.
            // Reuse the destination index instead of scanning the entire tree
            // for every candidate of every throw preview.
            float reach = WalkPhysics.Spacing * 2;
            int minX = Mathf.FloorToInt((stance.x - reach) / WalkPhysics.Spacing), maxX = Mathf.CeilToInt((stance.x + reach) / WalkPhysics.Spacing);
            int minZ = Mathf.FloorToInt((stance.z - reach) / WalkPhysics.Spacing), maxZ = Mathf.CeilToInt((stance.z + reach) / WalkPhysics.Spacing);
            for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
            {
                List<WalkCell> bucket;
                if (reachableByXZ.TryGetValue(((long)x << 32) ^ (uint)z, out bucket))
                    foreach (WalkCell cell in bucket) best = ConnectorCost(cell, stance, best);
            }
            // Portal exits use their live endpoint, rather than a lattice pose.
            foreach (WalkCell cell in reachablePortals) best = ConnectorCost(cell, stance, best);
            return best;
        }
        private float ConnectorCost(WalkCell cell, Vector3 stance, float best)
        {
            Vector3 p = physics.Point(cell); float distance = Vector3.Distance(p, stance);
            if (distance > WalkPhysics.Spacing * 2) return best;
            float cost = search.Distances[cell] + distance;
            return cost < best && physics.Segment(p, stance) ? cost : best;
        }
        private WalkTree indexedTree;
        private int indexedCount;
        private void ClearReachableIndex()
        {
            indexedTree = null;
            indexedCount = 0;
            reachableByXZ.Clear();
            reachablePortals.Clear();
        }
        private void IndexReachableCells()
        {
            if (indexedTree == search && indexedCount == search.Settled.Count) return;
            ClearReachableIndex();
            indexedTree = search;
            indexedCount = search.Settled.Count;
            foreach (WalkCell cell in search.Settled)
            {
                if (cell.Portal != 0) { reachablePortals.Add(cell); continue; }
                long key = ((long)cell.X << 32) ^ (uint)cell.Z; List<WalkCell> bucket;
                if (!reachableByXZ.TryGetValue(key, out bucket)) { bucket = new List<WalkCell>(); reachableByXZ[key] = bucket; }
                bucket.Add(cell);
            }
        }
        public List<float> ReleaseHeights(Vector3 around, float range)
        {
            var values = new List<float> { physics.Position.y };
            if (search == null) return values;
            var nearbyHeights = new List<float>();
            foreach (WalkCell cell in search.Settled)
            {
                Vector3 point = physics.Point(cell); Vector3 delta = point - around; delta.y = 0;
                if (delta.magnitude > range) continue;
                bool found = false;
                foreach (float height in nearbyHeights) if (Mathf.Abs(height - point.y) < .15f) { found = true; break; }
                if (!found) nearbyHeights.Add(point.y);
            }
            nearbyHeights.Sort(delegate(float a, float b) { return Mathf.Abs(a - around.y).CompareTo(Mathf.Abs(b - around.y)); });
            foreach (float height in nearbyHeights) if (values.Count < 8 && Mathf.Abs(height - values[0]) > .15f) values.Add(height);
            return values;
        }
        public void Reset(string reason)
        {
            incrementalSearch = fastWarm = false; ClearRemote();
            ClearReachableIndex();
            Stop(reason); ResetGroundedMap(); teleportsCompleted = 0; lastDestination = arrivalDetails = null; identities.Clear(); nextSemanticScan = 0; available.Clear(); physics.Reset(); search = null; chef = null; lastPlan = -1; unavailable = reason;
        }
        public void Dispose() { ClearRemote(); }
        public void ResumeAfterPause(float seconds)
        {
            if (Action.State != "navigating") return;
            started += seconds; ResetWalkingProgress();
            if (facingSince >= 0) facingSince += seconds;
            if (settlingSince >= 0) settlingSince += seconds;
            if (activePortal != null) portalStarted += seconds;
            if (portalReplanUntil > 0) portalReplanUntil += seconds;
            lastPlan = -1; haveMotion = motionSampleValid = false; arrival.Reset();
        }
        public void Stop(string reason)
        {
            CancelRecoveryPlanning();
            Trace(reason, Vector3.zero, Vector3.zero);
            // Neutral first; callers must not observe idle with stale movement input.
            neutral();
            ResetWalkingProgress(); physics.ClearStallAvoidance();
            if (target != null && target.Object != null) lastDestination = Identity(target);
            fullCatalogueRequested = false;
            movementReference = movementWaiting = null;
            Action.Stop(reason); portalReplanUntil = 0; waitingPortalRoute = false; activePortal = null; portalSteps.Clear(); settlingSince = -1; target = null; route = null; facingSince = -1; arrival.Reset(); haveMotion = motionSampleValid = false;
        }
        public void CancelRecoveryPlanning() { recoveryTarget = null; }
        public void Invalidate(string reason)
        {
            incrementalSearch = fastWarm = false; ClearRemote();
            if (Action.State == "navigating") Stop(reason);
            available.Clear(); unavailable = reason;
            ClearReachableIndex();
        }
        private string CatalogueReason
        {
            get { return focusedPlan && (unavailable == null || unavailable == "planning_in_progress") ? "active_target_only" : unavailable; }
        }

        public object Describe()
        {
            return Json.Object("state", Action.State, "destinationId", Action.Destination, "reason", Action.Reason,
                "destination", target == null ? lastDestination : Identity(target),
                "movementReference", movementReference, "waitingReason", movementWaiting,
                "phase", Action.State == "idle" ? "idle" : movementWaiting != null ? "waiting_route" : activePortal != null ? portalPhase : waitingPortalRoute ? "waiting_portal_route" : settlingSince >= 0 ? (followingSupport ? "following_support" : "settling_ground") : facingSince < 0 ? "walking" : "aligning", "arrival", arrivalDetails,
                "teleportsCompleted", teleportsCompleted, "stallReroutes", stallReroutes, "portal", DescribePortal(),
                "executionTrace", physics.TraceEnabled ? executionTrace.ToArray() : null,
                "revision", Action.Revision, "catalogRevision", revision, "catalogReason", CatalogueReason,
                "catalogComplete", CatalogueReason == null && search != null && search.Complete,
                "catalogAgeSeconds", lastPlan < 0 ? null : (object)(Time.realtimeSinceStartup - lastPlan),
                "diagnostics", Json.Object("planMilliseconds", planMilliseconds, "prepareMilliseconds", prepareMilliseconds,
                    "searchMilliseconds", searchMilliseconds, "matchMilliseconds", matchMilliseconds, "searchNodes", nodeCount,
                    "searchGeneration", searchGeneration, "searchSlices", searchSlices, "searchReused", searchReused,
                    "searchReuseFailure", physics.ContinuityFailure, "startProofRevalidations", physics.StartProofRevalidations,
                    "searchBackend", remoteSearch != null ? "worker_process" : "in_process", "goalDirected", searchFocus != null,
                    "workerPid", WorkerHost.Current == null ? 0 : WorkerHost.Current.ProcessId,
                    "searchComplete", search != null && search.Complete,
                    "physicsQueries", physics.Queries, "candidateCount", candidateCount, "startCount", startCount, "observationError", observationError,
                    "cachedProbes", physics.CachedProbeCount, "cachedEdges", physics.CachedEdgeCount, "geometryCacheEvictions", physics.GeometryCacheEvictions,
                    "geometryChanges", physics.TraceEnabled ? physics.GeometryChanges.ToArray() : null,
                    "portals", physics.PortalDiagnostics()));
        }
        private static object Identity(Destination d)
        {
            if (d.IsPoint) return Json.Object("id",d.Id,"label",d.Kind == "platform" ? "Selected platform" : "Selected floor position","kind",d.Kind,"position",Values.Vector(d.Approach));
            return d.Object == null ? null : Json.Object("id", d.Id, "label", d.Object.name, "kind", d.Kind,
                "foodName", FoodName(d.Object),
                "objectInstanceId", d.Object.GetInstanceID(), "position", Values.Vector(d.Object.transform.position));
        }
        private static string FoodName(GameObject go)
        {
            var spawner = go.GetComponent<PickupItemSpawner>();
            return spawner == null || spawner.m_itemPrefab == null ? null : spawner.m_itemPrefab.name;
        }
        public object Available(PlayerControls player)
        {
            warmTicks = 32;
            // Observation must not replan an in-flight action. Tick owns the
            // ground-settling/transport checks and only replans at a usable pose.
            // A harness catalogue poll during a native stair hop otherwise turns
            // a recoverable contact transition into an immediate not_grounded stop.
            if (Action.State != "navigating")
            {
                Refresh(player, true, false, true);
            }
            // Observation never drives a replan during a stair hop or transport.
            // Once a caller requests the full list, Tick maintains it for this walk.
            else fullCatalogueRequested = true;
            var destinations = new List<object>();
            foreach (Destination d in available.Values)
            {
                if (d.IsPoint) continue; // Generated release stances are internal, never wire target IDs.
                int portalCount = 0; var portalIds = new List<int>();
                var cells = search == null ? null : search.Route(d.Goal);
                if (cells != null) foreach (WalkCell cell in cells) if (cell.Portal != 0) { portalCount++; portalIds.Add(cell.Portal); }
                Vector3 offset = d.Object.transform.position - physics.Position; offset.y = 0;
                destinations.Add(Json.Object("id", d.Id, "label", d.Object.name, "kind", d.Kind,
                    "foodName", FoodName(d.Object),
                    "objectInstanceId", d.Object.GetInstanceID(), "position", Values.Vector(d.Object.transform.position),
                    "approachPosition", Values.Vector(d.Approach), "routeDistance", d.Distance, "routeTeleports", portalCount, "routePortalIds", portalIds, "directDistance", offset.magnitude));
            }
            destinations.Sort(delegate(object a, object b) { return String.CompareOrdinal((string)((Json.Record)a)["id"], (string)((Json.Record)b)["id"]); });
            var actions = new List<object> { Json.Object("name", "stop", "parameters", new Dictionary<string, object>()) };
            if (destinations.Count > 0 && Action.State == "idle")
                actions.Add(Json.Object("name", "navigate_to", "parameters", Json.Object("destination_id", Json.Object("type", "enum", "options", destinations))));
            return Json.Object("actions", actions, "navigation", Describe(), "destinations", destinations,
                "complete", CatalogueReason == null && search != null && search.Complete, "reason", CatalogueReason);
        }
        public string Start(PlayerControls player, int owner, string destination, bool recovering = false)
        {
            if (Action.State != "idle") return "action_in_progress";
            // Revalidate and reuse the idle tree when its exact continuity proof
            // still holds. Restarting here can lose a distant conveyor route
            // solely because one active search slice cannot rebuild the loop.
            // Recovery runs with neutral input and may need a fresh graph after
            // a target/geometry outage. Portal kitchens cannot reuse a frontier;
            // a small idle slice can starve the same distant goal indefinitely.
            Destination requested = null;
            if (recovering)
                foreach (Destination known in identities.Values)
                    if (known.Id == destination && known.Object != null && known.Object.activeInHierarchy) { requested = known; break; }
            // A stalled pickup/placement already identifies its next station.
            // Certify that route before spending the recovery slice matching the
            // whole kitchen, which may change again before a broad search ends.
            recoveryTarget = requested;
            Refresh(player, true, false, true, recovering ? RecoveryBudgetMilliseconds : PlanningBudgetMilliseconds, requested);
            Destination chosen;
            if (!available.TryGetValue(destination, out chosen)) return unavailable ?? "destination_unavailable";
            if (!Action.Start(owner, destination, true)) return "action_in_progress";
            recoveryTarget = null;
            target = chosen; lastDestination = Identity(chosen); started = Time.realtimeSinceStartup;
            ResetWalkingProgress(); stallReroutes = 0;
            arrivalDetails = null; arrival.Reset(); facingSince = -1; haveMotion = motionSampleValid = false;
            settlingSince = -1; teleportsCompleted = 0; executionTrace.Clear(); SetRoute(chosen); Trace("start", chosen.Approach, Vector3.zero); return null;
        }
        private bool ApplyPlan()
        {
            Destination current;
            if (!available.TryGetValue(Action.Destination ?? "", out current))
            {
                // Budget exhaustion is not evidence of a blocked route. Retain only
                // a fully rechecked remaining route; actual obstruction still stops
                // in this update. Never retain a route after observation failures.
                if (unavailable == "planning_in_progress" && RemainingRouteClear()) { waitingPortalRoute = false; return true; }
                // A moving native receiver freezes its destination when receiving,
                // after the route was planned. Its arrival can be on another
                // island until the receiver cycles back. Wait on checked support
                // and rebuild reachability; never walk the obsolete exit chord.
                if (portalReplanUntil == 0)
                    foreach (PortalLink portal in portalSteps.Values)
                        if (portal.HasMoved) { portalReplanUntil = Time.realtimeSinceStartup + 15f; break; }
                if ((unavailable == null || unavailable == "planning_in_progress") &&
                    Time.realtimeSinceStartup < portalReplanUntil && Time.realtimeSinceStartup - started < 45f)
                {
                    waitingPortalRoute = true;
                    Trace("waiting_portal_route", Vector3.zero, Vector3.zero);
                    move(Vector3.zero, WalkingCommand.Milliseconds(Time.unscaledDeltaTime));
                    return false;
                }
                if (WaitForGroundedRoute(unavailable ?? "route_blocked")) return false;
                Stop(unavailable ?? "destination_unreachable"); return false;
            }
            movementWaiting = null; waitingPortalRoute = false; portalReplanUntil = 0; target = current; SetRoute(current); return true;
        }
        private bool RemainingRouteClear()
        {
            if (target == null || target.Object == null || route == null || TargetMoved()) return false;
            Vector3 from = physics.Position; int surfaceHistory = 0;
            for (int i = waypoint; i < route.Count; i++)
            {
                PortalLink portal;
                if (portalSteps.TryGetValue(i, out portal))
                {
                    if (!physics.PortalGeometry(portal) || !physics.PortalEntry(from, portal, surfaceHistory) ||
                        Vector3.Distance(portal.Entry, route[i]) > .04f || i + 1 >= route.Count ||
                        Vector3.Distance(portal.Exit, route[i + 1]) > .04f) return false;
                    from = portal.Exit; surfaceHistory = 0; i++;
                    if (++i < route.Count)
                    {
                        if (!physics.PortalDeparture(from, route[i], portal, ref surfaceHistory)) return false;
                        from = route[i];
                    }
                    continue;
                }
                if (!physics.Segment(from, route[i], ref surfaceHistory)) return false;
                from = route[i];
            }
            return !physics.Overflow;
        }
        private void SetRoute(Destination chosen)
        {
            route = new List<Vector3>(); portalSteps.Clear();
            foreach (WalkCell cell in search.Route(chosen.Goal))
            {
                if (cell.Portal != 0)
                {
                    PortalLink portal = physics.Portal(cell.Portal);
                    portalSteps[route.Count] = portal; route.Add(portal.Entry);
                }
                route.Add(physics.Point(cell));
            }
            if (route.Count == 0 || Vector3.Distance(route[route.Count - 1], chosen.Approach) > .001f) route.Add(chosen.Approach);
            waypoint = 0;
        }
        public void WarmCatalogue(PlayerControls player)
        {
            // This observation-only entry point cannot execute an active action.
            if (Action.State != "idle") return;
            if (chef == player && (unavailable == "planning_budget_exceeded" || unavailable == "planning_in_progress") &&
                warmTicks > 0 && lastPlanFrame != Time.frameCount && (fastWarm || Time.realtimeSinceStartup - lastPlan >= .1f))
            { Refresh(player, true, false, true); warmTicks -= fastWarm ? 1 : 4; }
        }
        public void Tick(PlayerControls player)
        {
            followingSupport = false;
            if (Action.State != "navigating")
            {
                // Stable graphs continue their frontier once per render frame
                // in a short slice. Unversioned/dynamic graphs keep paced fresh
                // searches; a catalogue request never duplicates work in Tick.
                WarmCatalogue(player);
                return;
            }
            if (movementReference != null && movementWaiting != null && Time.realtimeSinceStartup - lastPlan < .25f) { neutral(); return; }
            if (player != chef || target == null || target.Object == null ||
                (!target.IsPoint && !Eligible(target.Object, player, out target.Kind, true))) { Stop("destination_unavailable"); return; }
            int commandMilliseconds = WalkingCommand.Milliseconds(Time.unscaledDeltaTime);
            if (commandMilliseconds == 0) { Stop("unsafe_update_interval"); return; }
            if (activePortal != null) { ResetWalkingProgress(); TickPortal(commandMilliseconds); return; }
            string error = physics.Refresh(player);
            if ((error == null || error == "not_grounded") && physics.NeedsGroundSettling())
            {
                ResetWalkingProgress();
                if (settlingSince < 0) settlingSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - settlingSince > 2f) { Stop("ground_contact_timeout"); return; }
                if (ContinueAboveSupport(commandMilliseconds)) return;
                Trace("settling_ground", Vector3.zero, Vector3.zero);
                move(Vector3.zero, commandMilliseconds);
                return;
            }
            if (error != null) { Invalidate(error); return; }
            if (settlingSince >= 0) { settlingSince = -1; lastPlan = -1; }
            // Any observed geometry change prompts a full reachability recheck in this
            // same tick, before emitting another input. Also poll dynamic affordances.
            if (physics.Changed || Time.realtimeSinceStartup - lastPlan >= .1f || TargetMoved())
            {
                Refresh(player, true, true, movementReference != null);
                if (!ApplyPlan()) return;
            }
            if (route == null || route.Count == 0) { Stop("destination_unreachable"); return; }
            Vector3 position = physics.Position;
            SampleMotion(position);
            if (movementReference == null && Time.realtimeSinceStartup - started > 45f) { Stop("navigation_timeout"); return; }
            if (arrival.ShouldSettle(Vector3.Distance(position, target.Approach)) || BrakeForArrival(position, commandMilliseconds))
            { Align(position, commandMilliseconds); return; }
            if (!RecoverIfStalled(position)) return;
            facingSince = -1; arrival.Reset(); arrivalDetails = null;
            Vector3 goal;
            bool exiting = physics.ExitClearance(position, out goal);
            if (!exiting && !TryWalkingGoal(position, out goal))
            {
                // Native contact and slope motion can displace the body from the
                // previously checked connector without changing static geometry.
                // Replan from the actual pose before declaring the target blocked.
                Refresh(player, true, true, movementReference != null);
                if (!ApplyPlan()) return;
                if (!TryWalkingGoal(position, out goal)) { Stop("route_blocked"); return; }
            }
            PortalLink nextPortal;
            if (!exiting && portalSteps.TryGetValue(waypoint, out nextPortal))
            {
                ResetWalkingProgress();
                activePortal = nextPortal; portalStarted = Time.realtimeSinceStartup; sawTeleport = sawReceive = false; portalBrakeFrames = nextPortal.StagingDistance < 1f ? 3 : 2;
                TickPortal(commandMilliseconds); return;
            }
            // Use the same drift compensation and actual next-step safety check
            // during the final approach, including when facing a counter.
            Vector3 direction = goal - position; direction.y = 0;
            Vector3 drift = player.WindReceiver == null ? Vector3.zero : player.WindReceiver.GetVelocity();
            if (player.SurfaceMovable != null) drift += player.SurfaceMovable.GetVelocity();
            float speed = physics.Speed(player.GroundCollider), x, z;
            // Slippery surfaces retain velocity. Countersteer the lateral error and
            // brake before a turn; do not pretend small input amplitudes slow walking.
            var surface = player.PhysicsSurface == null ? null : player.PhysicsSurface.Properties;
            Vector3 velocity = player.Motion.GetVelocityXZ();
            if (surface != null && surface.Slippiness > .05f)
            {
                Vector3 projected = position + (velocity + drift) * Mathf.Lerp(.04f, .3f, surface.Slippiness);
                if (!physics.Segment(position, projected)) { Stop("unsafe_sliding_momentum"); return; }
                direction -= velocity * Mathf.Lerp(.02f, .22f, surface.Slippiness);
            }
            if (!WalkingSteering.Direction(direction.x, direction.z, drift.x, drift.z, speed, out x, out z)) { Stop("flow_prevents_walking"); return; }
            Vector3 input = new Vector3(x, 0, z);
            Trace("steer", goal, input);
            float stepTime = Mathf.Max(commandMilliseconds / 1000f, Time.deltaTime);
            // Near the checked final stance validate the next actual walking step;
            // the longer lookahead would extend through the counter beyond the stop.
            float horizon = Vector3.Distance(position, target.Approach) < .6f ? stepTime : stepTime + .04f;
            Vector3 projectedStep = position + (input * speed + drift) * horizon;
            if (!physics.Segment(position, projectedStep))
            {
                // The extra lookahead can run past a safe turn and into a hole.
                // Require the actual next step; optional lookahead is not a reason
                // to cancel an otherwise supported route before its corner.
                projectedStep = position + (input * speed + drift) * stepTime;
                if (!physics.Segment(position, projectedStep))
                {
                    // A safe polyline does not guarantee that its current heading
                    // clears a moving ramp edge over the entire input window.
                    // Choose the smallest checked inward turn that still advances
                    // toward this waypoint; never emit an unchecked escape input.
                    bool found = false;
                    float[] turns = { .258819f, -.258819f, .5f, -.5f, .707107f, -.707107f, .866025f, -.866025f, 1f, -1f };
                    foreach (float sine in turns)
                    {
                        float cosine = (float)Math.Sqrt(Mathf.Max(0, 1 - sine * sine));
                        Vector3 alternative = new Vector3(input.x * cosine - input.z * sine, 0, input.x * sine + input.z * cosine);
                        Vector3 movement = alternative * speed + drift;
                        if (Vector3.Dot(movement, goal - position) <= 0) continue;
                        if (!physics.Segment(position, position + movement * horizon)) continue;
                        input = alternative; found = true; Trace("steer_adjusted", goal, input); break;
                    }
                    if (!found) { Stop("next_step_unsafe"); return; }
                }
            }
            // Native input can be read before this component's next Update.
            // A lease lasting exactly the preceding frame often expires there.
            // Extend only a fully checked straight continuation. Near arrival,
            // cap that window before the working stance, including one native
            // physics step after expiry. Braking still uses the original frame
            // horizon; a longer input lease is not a predicted arrival.
            {
                int continuation = WalkingCommand.ContinuationMilliseconds(commandMilliseconds);
                Vector3 movement = input * speed + drift;
                if (Vector3.Distance(position, target.Approach) <= .8f)
                {
                    Vector3 remaining = target.Approach - position; remaining.y = 0;
                    Vector3 relative = movement + parentVelocity - targetVelocity; relative.y = 0;
                    float untilClosest = relative.sqrMagnitude > .0001f ? Vector3.Dot(remaining, relative) / relative.sqrMagnitude : 0;
                    int beforeStance = (int)Math.Floor(Mathf.Max(0, untilClosest - Time.fixedDeltaTime) * 1000f);
                    continuation = Math.Min(continuation, Math.Max(commandMilliseconds, beforeStance));
                    // Native flow can change at a belt/volume boundary during
                    // this window. Keep its established final-approach timing,
                    // as with retained sliding momentum, rather than assuming
                    // the current velocity remains valid through that crossing.
                    if (drift.sqrMagnitude > .0001f || (surface != null && surface.Slippiness > .05f))
                        continuation = commandMilliseconds;
                }
                // A preceding native velocity may integrate for one fixed step
                // before the expired input is read and neutralized.
                Vector3 extended = position + movement * (continuation / 1000f + Time.fixedDeltaTime);
                if (continuation > commandMilliseconds && physics.Segment(position, extended)) commandMilliseconds = continuation;
            }
            RecordWalking(position, goal - position, commandMilliseconds);
            move(input, commandMilliseconds);
        }
        private bool ContinueAboveSupport(int commandMilliseconds)
        {
            if (route == null || target == null || TargetMoved()) return false;
            Vector3 remaining = target.Approach - physics.Position; remaining.y = 0;
            if (remaining.magnitude < .8f) return false;
            int limit = Math.Min(route.Count - 1, waypoint + 5);
            for (int i = waypoint; i <= limit; i++) if (portalSteps.ContainsKey(i)) { limit = i - 1; break; }
            for (int i = limit; i >= waypoint; i--)
            {
                Vector3 input;
                int continuation = WalkingCommand.ContinuationMilliseconds(commandMilliseconds);
                if (physics.TryLandingWalk(route[i], Mathf.Max(continuation / 1000f, Time.deltaTime), out input))
                    commandMilliseconds = continuation;
                else if (!physics.TryLandingWalk(route[i], Mathf.Max(commandMilliseconds / 1000f, Time.deltaTime), out input)) continue;
                waypoint = i;
                followingSupport = true;
                Trace("following_support", route[i], input); move(input, commandMilliseconds); return true;
            }
            return false;
        }
        private bool TryWalkingGoal(Vector3 position, out Vector3 goal)
        {
            int limit = route.Count - 1;
            for (int i = waypoint; i < route.Count; i++) if (portalSteps.ContainsKey(i)) { limit = i; break; }
            waypoint = WalkSegments.Advance(waypoint, route.Count, delegate(int index)
            {
                Vector3 offset = route[index] - position;
                return WalkHeight.WaypointReached(offset.x, offset.y, offset.z, physics.StepHeight);
            }, delegate(int index) { return index <= limit && RouteSegment(position, index); });
            goal = route[waypoint];
            // Shortcut only across freshly checked supported space; no corner cutting.
            // A belt corner changes native drift after crossing its border.
            // Follow its checked turns before looking ahead: aiming several
            // nodes past the turn can countersteer against the entering belt
            // for one native frame after that belt has stopped applying.
            for (int i = physics.OnConveyor ? waypoint : Math.Min(limit, waypoint + 3); i > waypoint; i--)
                if (Vector3.Distance(position, route[i]) < (portalSteps.ContainsKey(i) ? 2f : 1.4f) && RouteSegment(position, i))
                { waypoint = i; goal = route[i]; break; }
            return RouteSegment(position, waypoint);
        }
        private bool BrakeForArrival(Vector3 position, int commandMilliseconds)
        {
            Vector3 delta = target.Approach - position;
            if (delta.magnitude > .6f) return false;
            Vector3 velocity = chef.Motion.GetVelocityXZ();
            if (chef.WindReceiver != null) velocity += chef.WindReceiver.GetVelocity();
            if (chef.SurfaceMovable != null) velocity += chef.SurfaceMovable.GetVelocity();
            velocity += parentVelocity - targetVelocity;
            float window = Mathf.Max(Time.fixedDeltaTime, Mathf.Max(commandMilliseconds / 1000f, Time.deltaTime));
            // The preceding native motion can still integrate before neutral
            // takes effect. Brake on its predicted arrival, but only actual pose,
            // speed and selection can satisfy Align's completion checks.
            return Vector3.Dot(delta, velocity) > 0 &&
                (delta - velocity * window).magnitude <= ArrivalCheck.PositionTolerance;
        }
        private bool TargetMoved()
        {
            if (target == null || route == null || route.Count == 0) return true;
            if (target.IsPoint)
            {
                Vector3 point; Collider support;
                if (target.PointSupport == null || !target.PointSupport.enabled || !target.PointSupport.gameObject.activeInHierarchy) return true;
                Vector3 current = (target.PointAnchor ?? target.PointSupport.transform).TransformPoint(target.LocalApproach);
                return Vector3.Distance(current, target.Approach) > .001f ||
                    !(target.StrictPointArrival ? physics.StationaryStand(current, out point, out support) : physics.Stand(current, out point, out support)) ||
                    Vector3.Distance(point, current) > .04f;
            }
            foreach (Collider c in target.Colliders)
            {
                Vector3 stance;
                if (c != null && physics.Approach(target.Approach, c, out stance) && Vector3.Distance(stance, target.Approach) < .04f) return false;
            }
            return true;
        }
        private void SampleMotion(Vector3 position)
        {
            float now = Time.realtimeSinceStartup, elapsed = now - motionTime;
            Transform parent = chef.transform.parent;
            parentVelocity = Vector3.zero;
            motionSampleValid = haveMotion && elapsed > .00001f;
            if (motionSampleValid)
            {
                // DynamicLandscapeParenting carries the chef with its attach
                // transform, separately from Rigidbody and SurfaceMovable motion.
                // Compare those native velocities and the destination in one
                // world frame. Never interpret a reparent as physical velocity.
                if (parent != null && parent == motionParent)
                {
                    parentVelocity = (parent.TransformPoint(motionParentPosition) - motionPosition) / elapsed;
                    parentVelocity.y = 0;
                }
                // Transport the previous chef pose with the destination. This
                // measures translation/rotation of a shared moving platform
                // without mistaking it for movement past the target.
                Transform anchor = target.IsPoint ? (target.PointAnchor ?? target.PointSupport.transform) : target.Object.transform;
                Vector3 transported = anchor.TransformPoint(motionLocalPosition);
                targetVelocity = (transported - motionPosition) / elapsed; targetVelocity.y = 0;
                Vector3 relative = position - transported; relative.y = 0;
                observedRelativeSpeed = relative.magnitude / elapsed;
            }
            else { targetVelocity = Vector3.zero; observedRelativeSpeed = Single.PositiveInfinity; }
            Transform support = target.IsPoint ? (target.PointAnchor ?? target.PointSupport.transform) : target.Object.transform;
            motionPosition = position; motionLocalPosition = support.InverseTransformPoint(position);
            motionParent = parent;
            if (parent != null) motionParentPosition = parent.InverseTransformPoint(position);
            motionTime = now; haveMotion = true;
        }
        private void Align(Vector3 position, int commandMilliseconds)
        {
            float now = Time.realtimeSinceStartup;
            if (facingSince < 0) facingSince = now;
            if (now - facingSince > 1.5f) { Stop("alignment_failed"); return; }
            Vector3 direction = target.Object.transform.position - position; direction.y = 0;
            if (!target.MovementOnly && direction.sqrMagnitude < .001f) { Stop("invalid_facing_target"); return; }
            Vector3 drift = chef.WindReceiver == null ? Vector3.zero : chef.WindReceiver.GetVelocity();
            if (chef.SurfaceMovable != null) drift += chef.SurfaceMovable.GetVelocity();
            Vector3 velocity = chef.Motion.GetVelocityXZ();
            // Neutral input stops ordinary walking; only slippery floors retain
            // locomotion velocity. Projecting the preceding full-speed command
            // here falsely runs through a counter before neutral has been applied.
            var surface = chef.PhysicsSurface == null ? null : chef.PhysicsSurface.Properties;
            float retainedMotion = surface == null ? 0 : Mathf.Clamp01(surface.Slippiness);
            float horizon = Mathf.Max(commandMilliseconds / 1000f, Time.deltaTime);
            if (!physics.Segment(position, position + (velocity * retainedMotion + drift) * horizon)) { Stop("unsafe_alignment_stance"); return; }
            // The game's axis helper normalizes every nonzero input to full speed.
            // Use its native turn helper at the normal turn rate with neutral walking
            // input, instead of walking into the counter just to change heading.
            Trace("aligning", target.Approach, Vector3.zero);
            move(Vector3.zero, commandMilliseconds);
            if (!target.MovementOnly) PlayerControlsHelper.TurnTowardsDirection(chef.gameObject, direction.normalized,
                chef.Movement.TurnSpeed, Mathf.Min(Time.deltaTime, .05f));
            float distance = Vector3.Distance(position, target.Approach);
            float dot = target.MovementOnly ? 1 : Vector3.Dot(chef.transform.forward.normalized, direction.normalized);
            bool selected = target.IsPoint || Selected(target.Object), canPickup, canPlace, canUse;
            Readiness(out canPickup, out canPlace, out canUse);
            bool ready = target.IsPoint || canPickup || canPlace || canUse;
            // Wind and surface velocities describe requested displacement; native
            // contacts and MovePosition integration can suppress it. Require a
            // sustained observed rest relative to the target, not zero ambient
            // force. Pending Rigidbody walking still prevents station arrival.
            // The projected drift above independently retains the footing check.
            float relativeSpeed = target.IsPoint && motionSampleValid ? observedRelativeSpeed : Mathf.Max(observedRelativeSpeed, velocity.magnitude);
            // Throw certification reserves PositionTolerance, not the wider
            // station-settling hysteresis. Refine an overshot generated stance.
            if (target.StrictPointArrival && distance > ArrivalCheck.PositionTolerance) arrival.Reset();
            if (target.MovementOnly)
            {
                bool platform; Transform support = SupportOwner(chef.GroundCollider, chef.gameObject, out platform);
                selected = support == (target.PointAnchor ?? target.PointSupport.transform) && Mathf.Abs(position.y-target.Approach.y) <= .12f;
            }
            bool positioned = arrival.Observe(now, distance, dot, relativeSpeed, selected);
            arrivalDetails = Json.Object("stanceError", distance, "facingErrorDegrees", Mathf.Acos(Mathf.Clamp(dot, -1, 1)) * Mathf.Rad2Deg,
                "relativeSpeed", motionSampleValid ? (object)relativeSpeed : null,
                "nativeSelected", selected, "positioned", positioned, "canPickup", canPickup, "canPlace", canPlace, "canUse", canUse, "readyForAction", positioned && ready);
            if (positioned) Stop(ready ? "arrived" : "positioned_no_action");
        }
        private void Readiness(out bool pickup, out bool place, out bool use)
        {
            var observed = chef.CurrentInteractionObjects;
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>();
            GameObject held = carrier == null ? null : carrier.InspectCarriedItem();
            bool matches = Matches(observed.m_TheOriginalHandlePickup, target.Object);
            pickup = carrier != null && held == null && matches && observed.m_iHandlePickup != null && observed.m_iHandlePickup.CanHandlePickup(carrier);
            Vector3 forward = chef.transform.forward;
            place = carrier != null && held != null && matches && observed.m_iHandlePlacement != null &&
                observed.m_iHandlePlacement.CanHandlePlacement(carrier, new Vector2(forward.x, forward.z), new PlacementContext(PlacementContext.Source.Player));
            use = observed.m_interactable != null && Matches(observed.m_interactable.gameObject, target.Object) && observed.m_interactable.CanInteract(chef.gameObject);
        }
        private bool Selected(GameObject go)
        {
            var observed = chef.CurrentInteractionObjects;
            bool selected = Matches(observed.m_TheOriginalHandlePickup, go) ||
                (observed.m_interactable != null && Matches(observed.m_interactable.gameObject, go));
            // A fixed station can be selected by the native grid while it has no
            // work or pickup available (for example, an empty sink). Items still
            // require their own pickup/interaction selection, not a nearby counter.
            var grid = observed.m_gridLocation as Component;
            if (target.Kind != "item" && grid != null && Matches(grid.gameObject, go)) selected = true;
            if (!selected) return false;
            foreach (Collider c in target.Colliders) if (c != null && physics.Sight(physics.Position, c)) return true;
            return false;
        }
        private static bool Matches(GameObject selected, GameObject targetObject)
        {
            return selected != null && (selected == targetObject || selected.transform.IsChildOf(targetObject.transform) || targetObject.transform.IsChildOf(selected.transform));
        }
        private static bool Eligible(GameObject go, PlayerControls player, out string kind, bool includeEmpty = false)
        {
            kind = null;
            if (go == null || !go.activeInHierarchy || go.transform.IsChildOf(player.transform)) return false;
            var interact = go.GetComponent<ClientInteractable>();
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(go);
            if (go.GetComponentInParent<PlayerControls>() != null && !(pickup is ClientBackpackDispenser)) return false;
            var placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(go);
            if (go.GetComponent<WashingStation>() != null) kind = "washing_station";
            else if (go.GetComponent<RubbishBin>() != null) kind = "disposal_station";
            else if (go.GetComponent<ServerWorkstation>() != null) kind = "workstation";
            else if (go.GetComponent<PickupItemSpawner>() != null) kind = "ingredient_supply";
            else if (go.GetComponent<CookingStation>() != null) kind = "cooking_station";
            else if (go.GetComponent<PlateReturnStation>() != null || go.GetComponent<PlateStackBase>() != null) kind = "plate_station";
            else if (go.GetComponent<PhysicalAttachment>() != null) kind = "item";
            else if (interact != null) kind = "interaction";
            else if (go.GetComponent<AttachStation>() != null) kind = "counter";
            else if (pickup != null || placement != null) kind = "pickup_or_placement";
            // Navigation answers whether this meaningful object can be approached.
            // Having dirty dishes, free hands or ingredients is a separate action
            // precondition, and must not hide a physically reachable destination.
            // Logistics must plan empty delivery surfaces before picking cargo
            // up, so walking discovery explicitly includes those surfaces too.
            if (kind == "counter" || kind == "pickup_or_placement")
            {
                if (includeEmpty) return placement != null || pickup != null;
                var carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
                return carrier != null && ((pickup != null && pickup.CanHandlePickup(carrier)) ||
                    (placement != null && carrier.InspectCarriedItem() != null));
            }
            return kind != null;
        }
        private List<Destination> Discover(PlayerControls player, Destination only = null)
        {
            var found = new Dictionary<int, Destination>();
            foreach (Collider collider in physics.Colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (only != null && !collider.transform.IsChildOf(only.Object.transform)) continue;
                Transform t = collider.transform;
                for (int depth = 0; t != null && depth < 5; depth++, t = t.parent)
                {
                    string kind;
                    if (!Eligible(t.gameObject, player, out kind, true)) continue;
                    if (only != null && t.gameObject != only.Object) break;
                    int key = t.gameObject.GetInstanceID(); Destination d;
                    if (!found.TryGetValue(key, out d))
                    {
                        if (!identities.TryGetValue(key, out d) || d.Object != t.gameObject)
                        { d = new Destination { Id = "d" + (++nextId), Object = t.gameObject }; identities[key] = d; }
                        d.Kind = kind; d.Pack = kind == "ingredient_supply" ? t.GetComponent<Backpack>() : null; d.Colliders.Clear(); found[key] = d;
                    }
                    d.Colliders.Add(collider); break;
                }
            }
            var removed = new List<int>();
            foreach (var pair in identities) if (pair.Value.Object == null) removed.Add(pair.Key);
            foreach (int key in removed) identities.Remove(key);
            return new List<Destination>(found.Values);
        }
        private void Refresh(PlayerControls player, bool force, bool alreadyRefreshed = false, bool incremental = false, double activeBudgetMilliseconds = PlanningBudgetMilliseconds, Destination requested = null)
        {
            if (planning) return;
            if (!force && Time.realtimeSinceStartup - lastPlan < .1f) return;
            planning = true; var watch = Stopwatch.StartNew();
            prepareMilliseconds = searchMilliseconds = matchMilliseconds = 0;
            nodeCount = candidateCount = startCount = 0;
            try
            {
                available.Clear(); chef = player;
                string error = alreadyRefreshed ? null : physics.Refresh(player);
                if (error != null) { ClearRemote(); incrementalSearch = fastWarm = false; unavailable = error; return; }
                if (target != null && target.IsPoint)
                {
                    if (target.PointSupport == null || !target.PointSupport.enabled || !target.PointSupport.gameObject.activeInHierarchy)
                    { unavailable = "release_support_unavailable"; return; }
                    target.Approach = (target.PointAnchor ?? target.PointSupport.transform).TransformPoint(target.LocalApproach);
                    Vector3 checkedPoint; Collider support;
                    bool supported = target.StrictPointArrival ? physics.StationaryStand(target.Approach, out checkedPoint, out support) : physics.Stand(target.Approach, out checkedPoint, out support);
                    bool platform;
                    if (!supported || Vector3.Distance(checkedPoint, target.Approach) > .04f ||
                        (target.MovementOnly && SupportOwner(support, player.gameObject, out platform) != target.PointAnchor)) { unavailable = "unsafe_release_stance"; return; }
                }
                observationError = null;
                // A read-only catalogue poll must not replace an unfinished
                // owned recovery search with a different broad frontier.
                Destination station = requested ?? target ?? recoveryTarget;
                focusedPlan = station != null && !station.IsPoint && (requested != null || recoveryTarget != null || !fullCatalogueRequested);
                var candidates = target != null && target.IsPoint ? new List<Destination> { target } : Discover(player, focusedPlan ? station : null);
                candidateCount = candidates.Count;
                StationMatch focused = focusedPlan && candidates.Count == 1 ? new StationMatch(this, station) : null;
                StationMatch nextFocus = physics.HasPortals ? null : focused;
                bool hadIncremental = incrementalSearch;
                bool sameFocus = nextFocus == null ? searchFocus == null : nextFocus.SameRegion(searchFocus);
                searchReused = incremental && incrementalSearch && search != null && sameFocus && physics.CanContinueSearch();
                List<WalkEdge> starts = null;
                if (!searchReused)
                {
                    starts = new List<WalkEdge>(physics.Starts());
                    if (starts.Count == 0) { incrementalSearch = fastWarm = false; unavailable = "no_safe_start"; return; }
                }
                startCount = starts == null ? savedStartCount : starts.Count;
                prepareMilliseconds = watch.Elapsed.TotalMilliseconds;
                var searchWatch = Stopwatch.StartNew();
                double budget = requested != null ? activeBudgetMilliseconds :
                    incremental && (searchReused || !hadIncremental) ? IncrementalBudgetMilliseconds : activeBudgetMilliseconds;
                Func<bool> hasBudget = delegate { return searchWatch.Elapsed.TotalMilliseconds < budget && !physics.Overflow; };
                WorkerHost worker = WorkerHost.Current;
                bool workerReady = worker != null && worker.Ready;
                if (remoteSearch != null && (remoteSearch.Failed || !workerReady)) searchReused = false;
                if (!searchReused)
                {
                    ClearRemote();
                    if (starts == null) starts = new List<WalkEdge>(physics.Starts());
                    searchGeneration++; searchSlices = 0;
                    // A fresh native slice keeps immediate certified options when
                    // animation/held-object motion invalidates continuity each frame.
                    searchFocus = nextFocus;
                    var local = new WalkSearch(starts, physics.Neighbours, 16000,
                        heuristic: nextFocus == null ? (Func<WalkCell, float>)null : nextFocus.LowerBound);
                    search = local;
                    if (focused == null) local.Continue(hasBudget);
                    else local.Continue(() => hasBudget() && local.MinimumPendingDistance < focused.CertificationCost, focused.Visit);
                }
                else if (remoteSearch == null)
                {
                    if (nextFocus != null)
                    {
                        focused.VisitIndexed();
                        var local = (WalkSearch)search;
                        local.Continue(() => hasBudget() && local.MinimumPendingDistance < focused.CertificationCost, focused.Visit);
                    }
                    else if (incremental && workerReady && !search.Complete && !search.NodeLimitReached)
                    {
                        starts = new List<WalkEdge>(physics.Starts());
                        remoteSearch = new RemoteWalk(worker, ++searchGeneration, starts.ToArray());
                    }
                    else ((WalkSearch)search).Continue(hasBudget);
                }
                if (remoteSearch != null)
                {
                    remoteSearch.Continue(physics.Neighbours, hasBudget);
                    // Keep already certified local results while the worker warms.
                    // Only replace them with an equally advanced certified frontier.
                    if (remoteSearch.Tree.MinimumPendingDistance >= search.MinimumPendingDistance)
                        search = remoteSearch.Tree;
                    if (!physics.RememberSearch(true))
                    {
                        ClearRemote(); searchReused = false;
                        starts = new List<WalkEdge>(physics.Starts());
                        search = new WalkSearch(starts, physics.Neighbours, 16000, hasBudget);
                    }
                }
                searchSlices++; savedStartCount = startCount;
                incrementalSearch = incremental;
                searchMilliseconds = searchWatch.Elapsed.TotalMilliseconds;
                nodeCount = search is WalkSearch ? search.Distances.Count : search.DiscoveredCount;
                if (physics.Overflow) { incrementalSearch = fastWarm = false; unavailable = "physics_query_overflow"; return; }
                IndexReachableCells();
                foreach (Destination d in candidates)
                {
                    if (d.IsPoint)
                    {
                        float pointBest = Single.MaxValue; WalkCell pointGoal = new WalkCell();
                        foreach (WalkCell cell in search.Settled)
                        {
                            Vector3 point = physics.Point(cell); float connector = Vector3.Distance(point, d.Approach);
                            if (connector > WalkPhysics.Spacing * 2) continue;
                            float cost = search.Distances[cell] + connector;
                            int surfaceHistory = cell.SurfaceHistory;
                            if (cost < pointBest && physics.Segment(point, d.Approach, ref surfaceHistory)) { pointBest = cost; pointGoal = cell; }
                        }
                        if (pointBest < Single.MaxValue)
                        { d.Goal = pointGoal; d.Distance = pointBest; available[d.Id] = d; }
                        continue;
                    }
                    var match = focused ?? new StationMatch(this, d);
                    match.VisitIndexed();
                    match.Publish();

                }
                if (physics.Overflow) { available.Clear(); ClearReachableIndex(); unavailable = "physics_query_overflow"; }
                else unavailable = search.Complete ? null : "planning_in_progress";
                bool reusable = incremental && !physics.Overflow && physics.RememberSearch(searchReused || remoteSearch != null);
                fastWarm = reusable && !search.NodeLimitReached && (searchReused || !hadIncremental);
                if (search.NodeLimitReached) warmTicks = 0;
                matchMilliseconds = watch.Elapsed.TotalMilliseconds - prepareMilliseconds - searchMilliseconds;
            }
            catch (Exception e)
            {
                ClearRemote(); incrementalSearch = fastWarm = false;
                available.Clear(); ClearReachableIndex(); unavailable = "navigation_observation_error"; observationError = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (unavailable != null && unavailable != "planning_in_progress")
                    ClearReachableIndex();
                watch.Stop();
                planMilliseconds = watch.Elapsed.TotalMilliseconds;
                lastPlan = Time.realtimeSinceStartup;
                lastPlanFrame = Time.frameCount;
                revision++;
                planning = false;
            }
        }
    }
}
