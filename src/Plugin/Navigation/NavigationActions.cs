using System;
using System.Collections.Generic;
using System.Diagnostics;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed class NavigationActions
    {
        private sealed class Destination
        {
            public string Id, Kind;
            public GameObject Object;
            public readonly List<Collider> Colliders = new List<Collider>();
            public WalkCell Goal;
            public Vector3 Approach;
            public float Distance;
        }
        private readonly WalkPhysics physics = new WalkPhysics();
        public readonly ChefActionState Action = new ChefActionState();
        private readonly Dictionary<string, Destination> available = new Dictionary<string, Destination>();
        private readonly Dictionary<int, Destination> identities = new Dictionary<int, Destination>();
        private readonly Action<Vector3> move;
        private readonly Action neutral;
        private WalkSearch search;
        private List<Vector3> route;
        private Destination target;
        private object lastDestination;
        private object arrivalDetails;
        private readonly ArrivalCheck arrival = new ArrivalCheck();
        private int nextId, waypoint, warmTicks;
        private PlayerControls chef;
        private float lastPlan = -1, started, lastProgress, facingSince = -1;
        private Vector3 progressPosition;
        private long revision;
        private string unavailable = "not_ready";
        private double planMilliseconds, prepareMilliseconds, searchMilliseconds, matchMilliseconds;
        private int nodeCount, candidateCount, startCount;
        private string observationError;
        private bool planning;
        public NavigationActions(Action<Vector3> walk, Action halt) { move = walk; neutral = halt; }
        public void Reset(string reason)
        {
            Stop(reason); lastDestination = arrivalDetails = null; identities.Clear(); available.Clear(); physics.Reset(); search = null; chef = null; lastPlan = -1; unavailable = reason;
        }
        public void Stop(string reason)
        {
            // Neutral first; callers must not observe idle with stale movement input.
            neutral();
            if (target != null && target.Object != null) lastDestination = Identity(target);
            Action.Stop(reason); target = null; route = null; facingSince = -1; arrival.Reset();
        }
        public void Invalidate(string reason)
        {
            if (Action.State == "navigating") Stop(reason);
            available.Clear(); unavailable = reason;
        }
        public object Describe()
        {
            return Json.Object("state", Action.State, "destinationId", Action.Destination, "reason", Action.Reason,
                "destination", target == null ? lastDestination : Identity(target),
                "phase", Action.State == "idle" ? "idle" : facingSince < 0 ? "walking" : "aligning", "arrival", arrivalDetails,
                "revision", Action.Revision, "catalogRevision", revision, "catalogReason", unavailable,
                "catalogComplete", unavailable == null && search != null && search.Complete,
                "catalogAgeSeconds", lastPlan < 0 ? null : (object)(Time.realtimeSinceStartup - lastPlan),
                "diagnostics", Json.Object("planMilliseconds", planMilliseconds, "prepareMilliseconds", prepareMilliseconds,
                    "searchMilliseconds", searchMilliseconds, "matchMilliseconds", matchMilliseconds, "searchNodes", nodeCount,
                    "physicsQueries", physics.Queries, "candidateCount", candidateCount, "startCount", startCount, "observationError", observationError));
        }
        private static object Identity(Destination d)
        {
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
            warmTicks = 8;
            Refresh(player, true);
            if (Action.State == "navigating") ApplyPlan();
            var destinations = new List<object>();
            foreach (Destination d in available.Values)
            {
                Vector3 offset = d.Object.transform.position - physics.Position; offset.y = 0;
                destinations.Add(Json.Object("id", d.Id, "label", d.Object.name, "kind", d.Kind,
                    "foodName", FoodName(d.Object),
                    "objectInstanceId", d.Object.GetInstanceID(), "position", Values.Vector(d.Object.transform.position),
                    "approachPosition", Values.Vector(d.Approach), "routeDistance", d.Distance, "directDistance", offset.magnitude));
            }
            destinations.Sort(delegate(object a, object b) { return String.CompareOrdinal((string)((Dictionary<string, object>)a)["id"], (string)((Dictionary<string, object>)b)["id"]); });
            var actions = new List<object> { Json.Object("name", "stop", "parameters", new Dictionary<string, object>()) };
            if (destinations.Count > 0 && Action.State == "idle")
                actions.Add(Json.Object("name", "navigate_to", "parameters", Json.Object("destination_id", Json.Object("type", "enum", "options", destinations))));
            return Json.Object("actions", actions, "navigation", Describe(), "destinations", destinations,
                "complete", unavailable == null && search != null && search.Complete, "reason", unavailable);
        }
        public string Start(PlayerControls player, int owner, string destination)
        {
            if (Action.State != "idle") return "action_in_progress";
            Refresh(player, true);
            Destination chosen;
            if (!available.TryGetValue(destination, out chosen)) return unavailable ?? "destination_unavailable";
            if (!Action.Start(owner, destination, true)) return "action_in_progress";
            target = chosen; lastDestination = Identity(chosen); started = lastProgress = Time.realtimeSinceStartup; progressPosition = physics.Position;
            arrivalDetails = null; arrival.Reset(); facingSince = -1;
            SetRoute(chosen); return null;
        }
        private bool ApplyPlan()
        {
            Destination current;
            if (!available.TryGetValue(Action.Destination ?? "", out current))
            { Stop(unavailable ?? "destination_unreachable"); return false; }
            target = current; SetRoute(current); return true;
        }
        private void SetRoute(Destination chosen)
        {
            route = new List<Vector3>(); foreach (WalkCell cell in search.Route(chosen.Goal)) route.Add(physics.Point(cell));
            if (route.Count == 0 || Vector3.Distance(route[route.Count - 1], chosen.Approach) > .001f) route.Add(chosen.Approach);
            waypoint = 0;
        }
        public void Tick(PlayerControls player)
        {
            if (Action.State != "navigating")
            {
                // A cold physics graph may take several bounded updates to warm.
                // Keep all destinations unavailable until a complete search passes.
                if (chef == player && (unavailable == "planning_budget_exceeded" || unavailable == "planning_in_progress") && warmTicks > 0)
                { warmTicks--; Refresh(player, true); }
                return;
            }
            if (player != chef || target == null || !Eligible(target.Object, player, out target.Kind)) { Stop("destination_unavailable"); return; }
            string error = physics.Refresh(player);
            if (error != null) { Invalidate(error); return; }
            // Any observed geometry change prompts a full reachability recheck in this
            // same tick, before emitting another input. Also poll dynamic affordances.
            if (physics.Changed || Time.realtimeSinceStartup - lastPlan >= .1f || TargetMoved())
            {
                Refresh(player, true, true);
                if (!ApplyPlan()) return;
            }
            if (route == null || route.Count == 0) { Stop("destination_unreachable"); return; }
            Vector3 position = physics.Position;
            if (Time.realtimeSinceStartup - started > 45f) { Stop("navigation_timeout"); return; }
            // A valid detour can initially move away from the destination. Measure
            // actual displacement for the stall watchdog, not Euclidean goal distance.
            if (Vector3.Distance(position, progressPosition) > .15f)
            { progressPosition = position; lastProgress = Time.realtimeSinceStartup; }
            if (Time.realtimeSinceStartup - lastProgress > 2f) { Stop("no_progress"); return; }
            if (Vector3.Distance(position, target.Approach) <= ArrivalCheck.PositionTolerance)
            { Align(position); return; }
            facingSince = -1; arrival.Reset(); arrivalDetails = null;
            while (waypoint < route.Count - 1 && Vector3.Distance(position, route[waypoint]) < .18f) waypoint++;
            Vector3 goal = route[waypoint];
            // Shortcut only across freshly checked supported space; no corner cutting.
            for (int i = Math.Min(route.Count - 1, waypoint + 3); i > waypoint; i--)
                if (Vector3.Distance(position, route[i]) < 1.4f && physics.Segment(position, route[i])) { waypoint = i; goal = route[i]; break; }
            if (!physics.Segment(position, goal)) { Stop("route_blocked"); return; }
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
            float stepTime = Mathf.Clamp(Time.unscaledDeltaTime, .0167f, .06f);
            // Near the checked final stance validate the next actual walking step;
            // the longer lookahead would extend through the counter beyond the stop.
            float horizon = Vector3.Distance(position, target.Approach) < .6f ? stepTime : stepTime + .04f;
            Vector3 projectedStep = position + (input * speed + drift) * horizon;
            if (!physics.Segment(position, projectedStep)) { Stop("next_step_unsafe"); return; }
            move(input);
        }
        private bool TargetMoved()
        {
            if (target == null || route == null || route.Count == 0) return true;
            foreach (Collider c in target.Colliders)
            {
                Vector3 stance;
                if (c != null && physics.Approach(target.Approach, c, out stance) && Vector3.Distance(stance, target.Approach) < .04f) return false;
            }
            return true;
        }
        private void Align(Vector3 position)
        {
            float now = Time.realtimeSinceStartup;
            if (facingSince < 0) facingSince = now;
            if (now - facingSince > 1.5f) { Stop("alignment_failed"); return; }
            Vector3 direction = target.Object.transform.position - position; direction.y = 0;
            if (direction.sqrMagnitude < .001f) { Stop("invalid_facing_target"); return; }
            Vector3 drift = chef.WindReceiver == null ? Vector3.zero : chef.WindReceiver.GetVelocity();
            if (chef.SurfaceMovable != null) drift += chef.SurfaceMovable.GetVelocity();
            Vector3 velocity = chef.Motion.GetVelocityXZ();
            if (!physics.Segment(position, position + (velocity + drift) * .08f)) { Stop("unsafe_alignment_stance"); return; }
            // The game's axis helper normalizes every nonzero input to full speed.
            // Use its native turn helper at the normal turn rate with neutral walking
            // input, instead of walking into the counter just to change heading.
            move(Vector3.zero);
            PlayerControlsHelper.TurnTowardsDirection(chef.gameObject, direction.normalized,
                chef.Movement.TurnSpeed, Mathf.Min(Time.deltaTime, .05f));
            float distance = Vector3.Distance(position, target.Approach);
            float dot = Vector3.Dot(chef.transform.forward.normalized, direction.normalized);
            bool selected = Selected(target.Object), canPickup, canPlace, canUse;
            Readiness(out canPickup, out canPlace, out canUse);
            bool ready = canPickup || canPlace || canUse;
            bool positioned = arrival.Observe(now, distance, dot, velocity.magnitude, selected);
            arrivalDetails = Json.Object("stanceError", distance, "facingErrorDegrees", Mathf.Acos(Mathf.Clamp(dot, -1, 1)) * Mathf.Rad2Deg,
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
        private static bool Eligible(GameObject go, PlayerControls player, out string kind)
        {
            kind = null;
            if (go == null || !go.activeInHierarchy || go.transform.IsChildOf(player.transform) || go.GetComponentInParent<PlayerControls>() != null) return false;
            var interact = go.GetComponent<ClientInteractable>();
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(go);
            var placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(go);
            if (go.GetComponent<WashingStation>() != null) kind = "washing_station";
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
            // Ordinary empty counters are useful only when carrying something to
            // place. Keep the catalogue focused on objects and task locations.
            if (kind == "counter" || kind == "pickup_or_placement")
            {
                var carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
                return carrier != null && ((pickup != null && pickup.CanHandlePickup(carrier)) ||
                    (placement != null && carrier.InspectCarriedItem() != null));
            }
            return kind != null;
        }
        private List<Destination> Discover(PlayerControls player)
        {
            var found = new Dictionary<int, Destination>();
            foreach (Collider collider in physics.Colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Transform t = collider.transform;
                for (int depth = 0; t != null && depth < 5; depth++, t = t.parent)
                {
                    string kind;
                    if (!Eligible(t.gameObject, player, out kind)) continue;
                    int key = t.gameObject.GetInstanceID(); Destination d;
                    if (!found.TryGetValue(key, out d))
                    {
                        if (!identities.TryGetValue(key, out d) || d.Object != t.gameObject)
                        { d = new Destination { Id = "d" + (++nextId), Object = t.gameObject }; identities[key] = d; }
                        d.Kind = kind; d.Colliders.Clear(); found[key] = d;
                    }
                    d.Colliders.Add(collider); break;
                }
            }
            var removed = new List<int>();
            foreach (var pair in identities) if (pair.Value.Object == null) removed.Add(pair.Key);
            foreach (int key in removed) identities.Remove(key);
            return new List<Destination>(found.Values);
        }
        private void Refresh(PlayerControls player, bool force, bool alreadyRefreshed = false)
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
                if (error != null) { unavailable = error; return; }
                observationError = null;
                List<Destination> candidates = Discover(player); candidateCount = candidates.Count;
                var starts = new List<WalkEdge>(physics.Starts()); startCount = starts.Count;
                if (startCount == 0) { unavailable = "no_safe_start"; return; }
                prepareMilliseconds = watch.Elapsed.TotalMilliseconds;
                var searchWatch = Stopwatch.StartNew();
                search = new WalkSearch(starts, physics.Neighbours, 16000,
                    delegate { return searchWatch.Elapsed.TotalMilliseconds < 12 && !physics.Overflow; });
                searchMilliseconds = searchWatch.Elapsed.TotalMilliseconds;
                nodeCount = search.Distances.Count;
                if (physics.Overflow) { unavailable = "physics_query_overflow"; return; }
                // Index the reachable nodes once, then only inspect cells around each
                // meaningful object. Never emit the lattice itself as available actions.
                var byXZ = new Dictionary<long, List<WalkCell>>();
                foreach (WalkCell cell in search.Settled)
                {
                    long key = ((long)cell.X << 32) ^ (uint)cell.Z; List<WalkCell> bucket;
                    if (!byXZ.TryGetValue(key, out bucket)) { bucket = new List<WalkCell>(); byXZ[key] = bucket; }
                    bucket.Add(cell);
                }
                foreach (Destination d in candidates)
                {
                    float best = Single.MaxValue; WalkCell goal = new WalkCell(); Vector3 bestApproach = Vector3.zero;
                    foreach (Collider c in d.Colliders)
                    {
                        Bounds b = c.bounds;
                        int minX = Mathf.FloorToInt((b.min.x - 1) / WalkPhysics.Spacing), maxX = Mathf.CeilToInt((b.max.x + 1) / WalkPhysics.Spacing);
                        int minZ = Mathf.FloorToInt((b.min.z - 1) / WalkPhysics.Spacing), maxZ = Mathf.CeilToInt((b.max.z + 1) / WalkPhysics.Spacing);
                        if ((maxX - minX) * (maxZ - minZ) > 900) continue;
                        for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
                        {
                            List<WalkCell> bucket;
                            if (!byXZ.TryGetValue(((long)x << 32) ^ (uint)z, out bucket)) continue;
                            foreach (WalkCell cell in bucket)
                            {
                                float distance = search.Distances[cell]; Vector3 stance, point = physics.Point(cell);
                                if (distance >= best || !physics.Approach(point, c, out stance)) continue;
                                distance += Vector3.Distance(point, stance);
                                if (distance >= best) continue;
                                best = distance; goal = cell; bestApproach = stance;
                            }
                        }
                    }
                    // Any unsettled approach costs at least the frontier distance.
                    // Publish only when even an unseen approach cannot beat this one.
                    if (best != Single.MaxValue && best <= search.MinimumPendingDistance)
                    { d.Goal = goal; d.Approach = bestApproach; d.Distance = best; available[d.Id] = d; }
                }
                if (physics.Overflow) { available.Clear(); unavailable = "physics_query_overflow"; }
                else unavailable = search.Complete ? null : "planning_in_progress";
                matchMilliseconds = watch.Elapsed.TotalMilliseconds - prepareMilliseconds - searchMilliseconds;
            }
            catch (Exception e)
            {
                available.Clear(); unavailable = "navigation_observation_error"; observationError = e.GetType().Name + ": " + e.Message;
            }
            finally { watch.Stop(); planMilliseconds = watch.Elapsed.TotalMilliseconds; lastPlan = Time.realtimeSinceStartup; revision++; planning = false; }
        }
    }
}
