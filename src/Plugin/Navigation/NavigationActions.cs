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
        private int nextId, waypoint;
        private PlayerControls chef;
        private float lastPlan = -1, started, lastProgress, facingSince = -1;
        private Vector3 progressPosition;
        private long revision;
        private string unavailable = "not_ready";
        private double planMilliseconds;
        private int nodeCount, candidateCount, startCount;
        private string observationError;
        private bool planning;
        public NavigationActions(Action<Vector3> walk, Action halt) { move = walk; neutral = halt; }
        public void Reset(string reason)
        {
            Stop(reason); identities.Clear(); available.Clear(); physics.Reset(); search = null; chef = null; lastPlan = -1; unavailable = reason;
        }
        public void Stop(string reason)
        {
            // Neutral first; callers must not observe idle with stale movement input.
            neutral(); Action.Stop(reason); target = null; route = null; facingSince = -1;
        }
        public void Invalidate(string reason)
        {
            if (Action.State == "navigating") Stop(reason);
            available.Clear(); unavailable = reason;
        }
        public object Describe()
        {
            return Json.Object("state", Action.State, "destinationId", Action.Destination, "reason", Action.Reason,
                "revision", Action.Revision, "catalogRevision", revision, "catalogReason", unavailable,
                "catalogAgeSeconds", lastPlan < 0 ? null : (object)(Time.realtimeSinceStartup - lastPlan),
                "diagnostics", Json.Object("planMilliseconds", planMilliseconds, "searchNodes", nodeCount, "physicsQueries", physics.Queries, "candidateCount", candidateCount, "startCount", startCount, "observationError", observationError));
        }
        public object Available(PlayerControls player)
        {
            Refresh(player, true);
            if (Action.State == "navigating") ApplyPlan();
            var destinations = new List<object>();
            foreach (Destination d in available.Values)
                destinations.Add(Json.Object("id", d.Id, "label", d.Object.name, "kind", d.Kind,
                    "objectInstanceId", d.Object.GetInstanceID(), "position", Values.Vector(d.Object.transform.position),
                    "approachPosition", Values.Vector(physics.Point(d.Goal)), "routeDistance", d.Distance));
            destinations.Sort(delegate(object a, object b) { return String.CompareOrdinal((string)((Dictionary<string, object>)a)["id"], (string)((Dictionary<string, object>)b)["id"]); });
            var actions = new List<object> { Json.Object("name", "stop", "parameters", new Dictionary<string, object>()) };
            if (destinations.Count > 0 && Action.State == "idle")
                actions.Add(Json.Object("name", "navigate_to", "parameters", Json.Object("destination_id", Json.Object("type", "enum", "options", destinations))));
            return Json.Object("actions", actions, "navigation", Describe(), "destinations", destinations);
        }
        public string Start(PlayerControls player, int owner, string destination)
        {
            if (Action.State != "idle") return "action_in_progress";
            Refresh(player, true);
            Destination chosen;
            if (!available.TryGetValue(destination, out chosen)) return unavailable ?? "destination_unavailable";
            if (!Action.Start(owner, destination, true)) return "action_in_progress";
            target = chosen; started = lastProgress = Time.realtimeSinceStartup; progressPosition = physics.Position;
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
            waypoint = 0;
        }
        public void Tick(PlayerControls player)
        {
            if (Action.State != "navigating") return;
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
            if (Selected(target.Object)) { Stop("arrived"); return; }
            if (Time.realtimeSinceStartup - started > 45f) { Stop("navigation_timeout"); return; }
            // A valid detour can initially move away from the destination. Measure
            // actual displacement for the stall watchdog, not Euclidean goal distance.
            if (Vector3.Distance(position, progressPosition) > .15f)
            { progressPosition = position; lastProgress = Time.realtimeSinceStartup; }
            if (Time.realtimeSinceStartup - lastProgress > 2f) { Stop("no_progress"); return; }
            while (waypoint < route.Count - 1 && Vector3.Distance(position, route[waypoint]) < .18f) waypoint++;
            Vector3 goal = route[waypoint];
            if (waypoint == route.Count - 1 && Vector3.Distance(position, goal) < .2f)
            {
                if (facingSince < 0) facingSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - facingSince > .6f) { Stop("interaction_not_selected"); return; }
                // Native movement input also turns the chef. A short safe approach
                // pulse is needed; never rotate/teleport the chef behind the game.
                goal = target.Object.transform.position; goal.y = position.y;
                Vector3 facing = goal - position;
                if (!SafeFacing(position, facing)) { Stop("approach_blocked"); return; }
                move(facing.normalized); return;
            }
            facingSince = -1;
            // Shortcut only across freshly checked supported space; no corner cutting.
            for (int i = Math.Min(route.Count - 1, waypoint + 3); i > waypoint; i--)
                if (Vector3.Distance(position, route[i]) < 1.4f && physics.Segment(position, route[i])) { waypoint = i; goal = route[i]; break; }
            if (!physics.Segment(position, goal)) { Stop("route_blocked"); return; }
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
            Vector3 projectedStep = position + (input * speed + drift) * (stepTime + .04f);
            if (!physics.Segment(position, projectedStep)) { Stop("next_step_unsafe"); return; }
            move(input);
        }
        private bool TargetMoved()
        {
            if (target == null || route == null || route.Count == 0) return true;
            foreach (Collider c in target.Colliders) if (c != null && physics.Sight(route[route.Count - 1], c)) return false;
            return true;
        }
        private bool SafeFacing(Vector3 position, Vector3 direction)
        {
            if (direction.sqrMagnitude < .0001f) return false;
            // Allow facing into the destination's physical collider, but only when
            // the current supported stance remains safe and the target is in sight.
            Vector3 stand; Collider ground;
            if (!physics.Stand(position, out stand, out ground)) return false;
            float speed = physics.Speed(chef.GroundCollider);
            Vector3 next = position + direction.normalized * speed * Mathf.Clamp(Time.unscaledDeltaTime + .02f, .025f, .08f);
            if (!physics.Segment(position, next)) return false;
            foreach (Collider c in target.Colliders) if (c != null && physics.Sight(position, c)) return true;
            return false;
        }
        private bool Selected(GameObject go)
        {
            var observed = chef.CurrentInteractionObjects;
            return Matches(observed.m_TheOriginalHandlePickup, go) ||
                (observed.m_interactable != null && Matches(observed.m_interactable.gameObject, go));
        }
        private static bool Matches(GameObject selected, GameObject targetObject)
        {
            return selected != null && (selected == targetObject || selected.transform.IsChildOf(targetObject.transform) || targetObject.transform.IsChildOf(selected.transform));
        }
        private static bool Eligible(GameObject go, PlayerControls player, out string kind)
        {
            kind = null;
            if (go == null || !go.activeInHierarchy || go.transform.IsChildOf(player.transform) || go.GetComponentInParent<PlayerControls>() != null) return false;
            var carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
            var interact = go.GetComponent<ClientInteractable>();
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(go);
            var placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(go);
            if (go.GetComponent<WashingStation>() != null) kind = "washing_station";
            else if (go.GetComponent<ServerWorkstation>() != null) kind = "workstation";
            else if (go.GetComponent<PickupItemSpawner>() != null) kind = "ingredient_supply";
            else if (go.GetComponent<CookingStation>() != null) kind = "cooking_station";
            else if (go.GetComponent<PhysicalAttachment>() != null) kind = "item";
            else if (interact != null) kind = "interaction";
            else if (go.GetComponent<AttachStation>() != null) kind = "counter";
            else if (pickup != null || placement != null) kind = "pickup_or_placement";
            if (kind == null) return false;
            if (interact != null && interact.CanInteract(player.gameObject)) return true;
            if (carrier != null && pickup != null && pickup.CanHandlePickup(carrier)) return true;
            return placement != null && carrier != null && carrier.InspectCarriedItem() != null;
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
            try
            {
                available.Clear(); chef = player;
                string error = alreadyRefreshed ? null : physics.Refresh(player);
                if (error != null) { unavailable = error; return; }
                observationError = null;
                List<Destination> candidates = Discover(player); candidateCount = candidates.Count;
                var starts = new List<WalkEdge>(physics.Starts()); startCount = starts.Count;
                if (startCount == 0) { unavailable = "no_safe_start"; return; }
                search = new WalkSearch(starts, physics.Neighbours, 16000,
                    delegate { return watch.Elapsed.TotalMilliseconds < 12 && !physics.Overflow; });
                nodeCount = search.Distances.Count;
                if (!search.Complete || physics.Overflow) { unavailable = physics.Overflow ? "physics_query_overflow" : "planning_budget_exceeded"; return; }
                // Index the reachable nodes once, then only inspect cells around each
                // meaningful object. Never emit the lattice itself as available actions.
                var byXZ = new Dictionary<long, List<WalkCell>>();
                foreach (WalkCell cell in search.Distances.Keys)
                {
                    long key = ((long)cell.X << 32) ^ (uint)cell.Z; List<WalkCell> bucket;
                    if (!byXZ.TryGetValue(key, out bucket)) { bucket = new List<WalkCell>(); byXZ[key] = bucket; }
                    bucket.Add(cell);
                }
                foreach (Destination d in candidates)
                {
                    float best = Single.MaxValue; WalkCell goal = new WalkCell();
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
                                float distance = search.Distances[cell];
                                if (distance >= best || !physics.Sight(physics.Point(cell), c)) continue;
                                best = distance; goal = cell;
                            }
                        }
                    }
                    if (best != Single.MaxValue) { d.Goal = goal; d.Distance = best; available[d.Id] = d; }
                }
                if (physics.Overflow) { available.Clear(); unavailable = "physics_query_overflow"; }
                else unavailable = null;
            }
            catch (Exception e)
            {
                available.Clear(); unavailable = "navigation_observation_error"; observationError = e.GetType().Name + ": " + e.Message;
            }
            finally { watch.Stop(); planMilliseconds = watch.Elapsed.TotalMilliseconds; lastPlan = Time.realtimeSinceStartup; revision++; planning = false; }
        }
    }
}
