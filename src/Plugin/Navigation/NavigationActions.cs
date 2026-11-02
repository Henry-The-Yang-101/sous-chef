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
            public readonly List<Collider> Colliders = new List<Collider>();
            public WalkCell Goal;
            public Vector3 Approach, LocalApproach;
            public float Distance;
            public bool IsPoint;
        }
        private readonly WalkPhysics physics = new WalkPhysics();
        public readonly ChefActionState Action = new ChefActionState();
        private readonly Dictionary<string, Destination> available = new Dictionary<string, Destination>();
        private readonly Dictionary<int, Destination> identities = new Dictionary<int, Destination>();
        private readonly Action<Vector3, int> move;
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
        private Vector3 motionPosition, motionLocalPosition, targetVelocity;
        private float motionTime, observedRelativeSpeed;
        private bool haveMotion, motionSampleValid;
        private long revision;
        private string unavailable = "not_ready";
        private double planMilliseconds, prepareMilliseconds, searchMilliseconds, matchMilliseconds;
        private int nodeCount, candidateCount, startCount;
        private string observationError;
        private bool planning;
        private const double PlanningBudgetMilliseconds = 12;
        private float settlingSince = -1;
        private readonly Queue<object> executionTrace = new Queue<object>();
        private void Trace(string phase, Vector3 goal, Vector3 input)
        {
            if (!physics.TraceEnabled || chef == null) return;
            var ground = chef.GetComponent<GroundCast>();
            var points = new List<object>();
            if (route != null) for (int i = waypoint; i < Math.Min(route.Count, waypoint + 5); i++) points.Add(Values.Vector(route[i]));
            executionTrace.Enqueue(Json.Object("time", Time.realtimeSinceStartup, "frame", Time.frameCount,
                "phase", phase, "position", Values.Vector(physics.Position), "velocity", Values.Vector(chef.Motion.GetVelocity()),
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
            move = walk; neutral = halt;
            physics.TraceEnabled = Environment.GetEnvironmentVariable("SOUS_CHEF_NAV_TRACE") == "1";
        }
        public GameObject Resolve(string id)
        {
            foreach (Destination d in identities.Values) if (d.Id == id && d.Object != null && d.Object.activeInHierarchy) return d.Object;
            return null;
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
        public string RegisterItem(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return null;
            Destination d;
            if (!identities.TryGetValue(go.GetInstanceID(), out d) || !System.Object.ReferenceEquals(d.Object, go))
            { d = new Destination { Id = "d" + (++nextId), Object = go, Kind = "item" }; identities[go.GetInstanceID()] = d; }
            return d.Id;
        }
        public string StartPosition(PlayerControls player, int owner, GameObject aim, Vector3 position)
        {
            if (Action.State != "idle") return "action_in_progress";
            chef = player;
            var chosen = new Destination { Id = "throw_stance", Object = aim, Kind = "throw_stance", IsPoint = true, Approach = position };
            if (!Action.Start(owner, chosen.Id, true)) return "action_in_progress";
            settlingSince = -1; teleportsCompleted = 0; target = chosen; started = lastProgress = Time.realtimeSinceStartup; progressPosition = player.transform.position;
            arrivalDetails = null; arrival.Reset(); facingSince = -1; haveMotion = motionSampleValid = false;
            Refresh(player, true);
            if (!ApplyPlan()) return Action.Reason;
            return null;
        }
        public void PrepareStances(PlayerControls player) { Refresh(player, true); }
        public bool TryStance(Vector3 point, out Vector3 stance)
        { Collider ground; return physics.Stand(point, out stance, out ground); }
        public float RouteCost(Vector3 stance)
        {
            float best = Single.MaxValue;
            if (Vector3.Distance(physics.Position, stance) < .01f && physics.Segment(physics.Position, stance)) return 0;
            if (search == null) return best;
            foreach (WalkCell cell in search.Settled)
            {
                Vector3 p = physics.Point(cell); float distance = Vector3.Distance(p, stance);
                if (distance > WalkPhysics.Spacing * 2) continue;
                float cost = search.Distances[cell] + distance;
                if (cost < best && physics.Segment(p, stance)) best = cost;
            }
            return best;
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
            Stop(reason); teleportsCompleted = 0; lastDestination = arrivalDetails = null; identities.Clear(); available.Clear(); physics.Reset(); search = null; chef = null; lastPlan = -1; unavailable = reason;
        }
        public void Stop(string reason)
        {
            Trace(reason, Vector3.zero, Vector3.zero);
            // Neutral first; callers must not observe idle with stale movement input.
            neutral();
            if (target != null && target.Object != null) lastDestination = Identity(target);
            Action.Stop(reason); activePortal = null; portalSteps.Clear(); settlingSince = -1; target = null; route = null; facingSince = -1; arrival.Reset(); haveMotion = motionSampleValid = false;
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
                "phase", Action.State == "idle" ? "idle" : activePortal != null ? portalPhase : settlingSince >= 0 ? "settling_ground" : facingSince < 0 ? "walking" : "aligning", "arrival", arrivalDetails,
                "teleportsCompleted", teleportsCompleted, "portal", DescribePortal(),
                "executionTrace", physics.TraceEnabled ? executionTrace.ToArray() : null,
                "revision", Action.Revision, "catalogRevision", revision, "catalogReason", unavailable,
                "catalogComplete", unavailable == null && search != null && search.Complete,
                "catalogAgeSeconds", lastPlan < 0 ? null : (object)(Time.realtimeSinceStartup - lastPlan),
                "diagnostics", Json.Object("planMilliseconds", planMilliseconds, "prepareMilliseconds", prepareMilliseconds,
                    "searchMilliseconds", searchMilliseconds, "matchMilliseconds", matchMilliseconds, "searchNodes", nodeCount,
                    "physicsQueries", physics.Queries, "candidateCount", candidateCount, "startCount", startCount, "observationError", observationError,
                    "portals", physics.PortalDiagnostics()));
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
            if (activePortal == null)
            {
                Refresh(player, true);
                if (Action.State == "navigating") ApplyPlan();
            }
            var destinations = new List<object>();
            foreach (Destination d in available.Values)
            {
                if (d.IsPoint) continue; // Generated release stances are internal, never wire target IDs.
                int portalCount = 0;
                var cells = search == null ? null : search.Route(d.Goal);
                if (cells != null) foreach (WalkCell cell in cells) if (cell.Portal != 0) portalCount++;
                Vector3 offset = d.Object.transform.position - physics.Position; offset.y = 0;
                destinations.Add(Json.Object("id", d.Id, "label", d.Object.name, "kind", d.Kind,
                    "foodName", FoodName(d.Object),
                    "objectInstanceId", d.Object.GetInstanceID(), "position", Values.Vector(d.Object.transform.position),
                    "approachPosition", Values.Vector(d.Approach), "routeDistance", d.Distance, "routeTeleports", portalCount, "directDistance", offset.magnitude));
            }
            destinations.Sort(delegate(object a, object b) { return String.CompareOrdinal((string)((Json.Record)a)["id"], (string)((Json.Record)b)["id"]); });
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
                if (unavailable == "planning_in_progress" && RemainingRouteClear()) return true;
                Stop(unavailable ?? "destination_unreachable"); return false;
            }
            target = current; SetRoute(current); return true;
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
                        if (!physics.PortalSegment(from, route[i], portal.ExitTrigger, ref surfaceHistory)) return false;
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
        public void Tick(PlayerControls player)
        {
            if (Action.State != "navigating")
            {
                // A cold physics graph may take several bounded updates to warm.
                // Publish certified destinations while the remaining graph warms.
                if (chef == player && (unavailable == "planning_budget_exceeded" || unavailable == "planning_in_progress") && warmTicks > 0)
                { warmTicks--; Refresh(player, true); }
                return;
            }
            if (player != chef || target == null || target.Object == null ||
                (!target.IsPoint && !Eligible(target.Object, player, out target.Kind))) { Stop("destination_unavailable"); return; }
            int commandMilliseconds = WalkingCommand.Milliseconds(Time.unscaledDeltaTime);
            if (commandMilliseconds == 0) { Stop("unsafe_update_interval"); return; }
            if (activePortal != null) { TickPortal(commandMilliseconds); return; }
            string error = physics.Refresh(player);
            if ((error == null || error == "not_grounded") && physics.NeedsGroundSettling())
            {
                if (settlingSince < 0) settlingSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - settlingSince > 2f) { Stop("ground_contact_timeout"); return; }
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
                Refresh(player, true, true);
                if (!ApplyPlan()) return;
            }
            if (route == null || route.Count == 0) { Stop("destination_unreachable"); return; }
            Vector3 position = physics.Position;
            SampleMotion(position);
            if (Time.realtimeSinceStartup - started > 45f) { Stop("navigation_timeout"); return; }
            // A valid detour can initially move away from the destination. Measure
            // actual displacement for the stall watchdog, not Euclidean goal distance.
            if (Vector3.Distance(position, progressPosition) > .15f)
            { progressPosition = position; lastProgress = Time.realtimeSinceStartup; }
            if (arrival.ShouldSettle(Vector3.Distance(position, target.Approach)))
            { Align(position, commandMilliseconds); return; }
            if (Time.realtimeSinceStartup - lastProgress > 2f) { Stop("no_progress"); return; }
            facingSince = -1; arrival.Reset(); arrivalDetails = null;
            Vector3 goal;
            if (!TryWalkingGoal(position, out goal))
            {
                // Native contact and slope motion can displace the body from the
                // previously checked connector without changing static geometry.
                // Replan from the actual pose before declaring the target blocked.
                Refresh(player, true, true);
                if (!ApplyPlan()) return;
                if (!TryWalkingGoal(position, out goal)) { Stop("route_blocked"); return; }
            }
            PortalLink nextPortal;
            if (portalSteps.TryGetValue(waypoint, out nextPortal))
            {
                activePortal = nextPortal; portalStarted = Time.realtimeSinceStartup; sawTeleport = false; portalBrakeFrames = 2;
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
                if (!physics.Segment(position, projectedStep)) { Stop("next_step_unsafe"); return; }
            }
            move(input, commandMilliseconds);
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
            for (int i = Math.Min(limit, waypoint + 3); i > waypoint; i--)
                if (Vector3.Distance(position, route[i]) < 1.4f && RouteSegment(position, i)) { waypoint = i; goal = route[i]; break; }
            return RouteSegment(position, waypoint);
        }
        private bool TargetMoved()
        {
            if (target == null || route == null || route.Count == 0) return true;
            if (target.IsPoint)
            {
                Vector3 point; Collider support;
                return !physics.Stand(target.Approach, out point, out support) || Vector3.Distance(point, target.Approach) > .04f;
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
            motionSampleValid = haveMotion && elapsed > .00001f;
            if (motionSampleValid)
            {
                // Transport the previous chef pose with the destination. This
                // measures translation/rotation of a shared moving platform
                // without mistaking it for movement past the target.
                Vector3 transported = target.IsPoint ? motionPosition : target.Object.transform.TransformPoint(motionLocalPosition);
                targetVelocity = (transported - motionPosition) / elapsed; targetVelocity.y = 0;
                Vector3 relative = position - transported; relative.y = 0;
                observedRelativeSpeed = relative.magnitude / elapsed;
            }
            else { targetVelocity = Vector3.zero; observedRelativeSpeed = Single.PositiveInfinity; }
            motionPosition = position; motionLocalPosition = target.Object.transform.InverseTransformPoint(position);
            motionTime = now; haveMotion = true;
        }
        private void Align(Vector3 position, int commandMilliseconds)
        {
            float now = Time.realtimeSinceStartup;
            if (facingSince < 0) facingSince = now;
            if (now - facingSince > 1.5f) { Stop("alignment_failed"); return; }
            Vector3 direction = target.Object.transform.position - position; direction.y = 0;
            if (direction.sqrMagnitude < .001f) { Stop("invalid_facing_target"); return; }
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
            move(Vector3.zero, commandMilliseconds);
            PlayerControlsHelper.TurnTowardsDirection(chef.gameObject, direction.normalized,
                chef.Movement.TurnSpeed, Mathf.Min(Time.deltaTime, .05f));
            float distance = Vector3.Distance(position, target.Approach);
            float dot = Vector3.Dot(chef.transform.forward.normalized, direction.normalized);
            bool selected = target.IsPoint || Selected(target.Object), canPickup, canPlace, canUse;
            Readiness(out canPickup, out canPlace, out canUse);
            bool ready = target.IsPoint || canPickup || canPlace || canUse;
            Vector3 relativeVelocity = velocity + drift - targetVelocity; relativeVelocity.y = 0;
            float relativeSpeed = Mathf.Max(observedRelativeSpeed, relativeVelocity.magnitude);
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
            if (go == null || !go.activeInHierarchy || go.transform.IsChildOf(player.transform) || go.GetComponentInParent<PlayerControls>() != null) return false;
            var interact = go.GetComponent<ClientInteractable>();
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(go);
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
            // Ordinary empty counters are useful only when carrying something to
            // place. Keep the catalogue focused on objects and task locations.
            if (kind == "counter" || kind == "pickup_or_placement")
            {
                if (includeEmpty) return placement != null || pickup != null;
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
                    if (!Eligible(t.gameObject, player, out kind, true)) continue;
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
            var walkTargets = new List<Destination>();
            foreach (Destination d in found.Values) { string kind; if (Eligible(d.Object, player, out kind)) walkTargets.Add(d); }
            return walkTargets;
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
                List<Destination> candidates = target != null && target.IsPoint ? new List<Destination> { target } : Discover(player); candidateCount = candidates.Count;
                var starts = new List<WalkEdge>(physics.Starts()); startCount = starts.Count;
                if (startCount == 0) { unavailable = "no_safe_start"; return; }
                prepareMilliseconds = watch.Elapsed.TotalMilliseconds;
                var searchWatch = Stopwatch.StartNew();
                search = new WalkSearch(starts, physics.Neighbours, 16000,
                    delegate { return searchWatch.Elapsed.TotalMilliseconds < PlanningBudgetMilliseconds && !physics.Overflow; });
                searchMilliseconds = searchWatch.Elapsed.TotalMilliseconds;
                nodeCount = search.Distances.Count;
                if (physics.Overflow) { unavailable = "physics_query_overflow"; return; }
                // Index the reachable nodes once, then only inspect cells around each
                // meaningful object. Never emit the lattice itself as available actions.
                var byXZ = new Dictionary<long, List<WalkCell>>();
                foreach (WalkCell cell in search.Settled)
                {
                    if (cell.Portal != 0) continue;
                    long key = ((long)cell.X << 32) ^ (uint)cell.Z; List<WalkCell> bucket;
                    if (!byXZ.TryGetValue(key, out bucket)) { bucket = new List<WalkCell>(); byXZ[key] = bucket; }
                    bucket.Add(cell);
                }
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
                        { d.Goal = pointGoal; d.Distance = pointBest; d.LocalApproach = d.Object.transform.InverseTransformPoint(d.Approach); available[d.Id] = d; }
                        continue;
                    }
                    float best = Single.MaxValue; WalkCell goal = new WalkCell(); Vector3 bestApproach = Vector3.zero;
                    float preferred = Single.MaxValue; WalkCell preferredGoal = new WalkCell(); Vector3 preferredApproach = Vector3.zero;
                    // Do not chase a newly cheapest side/stance on every refresh
                    // while braking and turning. Retain the selected stance only
                    // while native geometry still validates it.
                    Vector3 retained = d.Object.transform.TransformPoint(d.LocalApproach);
                    bool retainApproach = false;
                    if (target == d && Vector3.Distance(physics.Position, retained) < 1f)
                        foreach (Collider c in d.Colliders)
                        {
                            Vector3 checkedStance;
                            if (physics.Approach(retained, c, out checkedStance) && Vector3.Distance(retained, checkedStance) < .04f)
                            { retainApproach = true; break; }
                        }
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
                                if ((distance >= best && (!retainApproach || distance >= preferred)) || !physics.Approach(point, c, out stance, cell.SurfaceHistory)) continue;
                                distance += Vector3.Distance(point, stance);
                                if (distance < best) { best = distance; goal = cell; bestApproach = stance; }
                                if (retainApproach && distance < preferred && Vector3.Distance(stance, retained) < .04f)
                                { preferred = distance; preferredGoal = cell; preferredApproach = stance; }
                            }
                        }
                    }
                    // A valid stance is a preference, not evidence that its side
                    // is reachable. Fall back to another certified approach when
                    // the retained route is blocked. An incomplete search can
                    // still preserve a freshly checked previous route.
                    if (retainApproach && preferred != Single.MaxValue && preferred <= search.MinimumPendingDistance)
                    { best = preferred; goal = preferredGoal; bestApproach = preferredApproach; }
                    else if (retainApproach && !search.Complete && RemainingRouteClear()) continue;
                    // Any unsettled approach costs at least the frontier distance.
                    // Publish only when even an unseen approach cannot beat this one.
                    if (best != Single.MaxValue && best <= search.MinimumPendingDistance)
                    {
                        Vector3 localApproach = d.Object.transform.InverseTransformPoint(bestApproach);
                        if (target == d && d.Object.transform.TransformVector(localApproach - d.LocalApproach).magnitude >= .04f)
                        { arrival.Reset(); facingSince = -1; arrivalDetails = null; }
                        d.Goal = goal; d.Approach = bestApproach; d.LocalApproach = localApproach; d.Distance = best; available[d.Id] = d;
                    }
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
