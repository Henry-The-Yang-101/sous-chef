using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Read native capabilities/state rather than item or kitchen name heuristics.
    internal static class TaskNative
    {
        public static T Component<T>(GameObject go) where T : Component
        { return go == null ? null : go.GetComponent<T>(); }

        public static Component ProcessingHandler(GameObject item, bool mixing)
        { return mixing ? (Component)Component<ServerMixingHandler>(item) : Component<ServerCookingHandler>(item); }

        public static object Call(object instance, string method, params object[] args)
        {
            if (instance == null) return null;
            var m = instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new InvalidOperationException("native_method_unavailable_" + method);
            return m.Invoke(instance, args);
        }
        public static string State(GameObject item, bool mixing = false)
        {
            Component handler = ProcessingHandler(item, mixing);
            if (handler == null) return null;
            object data = ActionObservations.Read<object>(handler, mixing ? "m_serverData" : "m_ServerData");
            return ActionObservations.Read<object>(data, mixing ? "m_mixingState" : "m_cookingState").ToString();
        }
        public static bool Ready(GameObject item)
        {
            string c = State(item), m = State(item, true);
            if (c == "Ruined" || m == "Ruined") return false;
            // A mixed dough currently baking is not ready merely because its
            // earlier mixing stage completed. Active cooking takes precedence.
            return c != null && c != "Idle" ? Complete(c) : Complete(m);
        }
        public static bool Ready(GameObject item, bool mixing)
        { return !Burnt(item) && Complete(State(item, mixing)); }
        private static GameObject StationFor(GameObject target)
        {
            if (Equipment(target)) return target;
            GameObject item = ActionObservations.Receiver(target);
            foreach (ServerAttachStation holder in UnityEngine.Object.FindObjectsOfType<ServerAttachStation>())
                if (System.Object.ReferenceEquals(holder.InspectItem(), item) && Equipment(holder.gameObject)) return holder.gameObject;
            return null;
        }
        public static bool IsMixer(GameObject target)
        {
            GameObject station = StationFor(target);
            return Component<ServerMixingStation>(station) != null || Component<MixingStation>(station) != null;
        }
        public static bool ReadyAt(GameObject target)
        {
            GameObject item = ActionObservations.Receiver(target);
            // Item targets and their parent station use the same observed holder
            // relationship. A bowl's unused cooking handler can say Progressing
            // at zero even while its mixing handler completes normally.
            GameObject station = StationFor(target);
            return station == null ? Ready(item) : Ready(item, IsMixer(station));
        }
        private static bool Complete(string state) { return state == "Completed" || state == "OverDoing"; }
        public static bool Burnt(GameObject item) { return State(item) == "Ruined" || State(item, true) == "Ruined"; }
        public static float Remaining(GameObject item, bool mixing = false, bool untilRuined = false)
        {
            Component h = ProcessingHandler(item, mixing);
            if (h == null) return Single.MaxValue;
            float progress = (float)Call(h, mixing ? "GetMixingProgress" : "GetCookingProgress");
            var property = h.GetType().GetProperty(mixing ? "AccessMixingTime" : "AccessCookingTime");
            return Math.Max(0, (float)property.GetValue(h, null) * (untilRuined ? 2 : 1) - progress);
        }
        public static bool Equipment(GameObject go)
        {
            // These native drivers advance food without a chef working there.
            // Their cooking-step definitions cover fryers, ovens, grills, pots,
            // heated cookers and mixers without a prefab/recipe allowlist.
            return go != null && (go.GetComponent<ServerCookingStation>() != null ||
                go.GetComponent<ServerMixingStation>() != null || go.GetComponent<ServerCookingRegion>() != null ||
                go.GetComponent<CookingStation>() != null || go.GetComponent<MixingStation>() != null);
        }
        public static bool Dirty(GameObject go) { return Component<ServerDirtyPlateStack>(go) != null; }
    }
    internal sealed partial class TaskActions
    {
        private readonly Dictionary<int, NavigationActions> observers = new Dictionary<int, NavigationActions>();
        private readonly Dictionary<int, float> humanDistances = new Dictionary<int, float>();
        private readonly Dictionary<string, CounterMotion> counterMotion = new Dictionary<string, CounterMotion>();
        private bool botSearchComplete, humanSearchComplete;
        private readonly HashSet<GameObject> deliveredItems = new HashSet<GameObject>();
        private PlayerControls Human(int player = 1)
        {
            PlayerControls found = null;
            foreach (PlayerControls p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p != chef && p.PlayerIDProvider != null && (int)p.PlayerIDProvider.GetID() + 1 == player)
                { if (found != null) return null; found = p; }
            return found;
        }
        private void RefreshWorld(int player = 0)
        {
            inventory.Refresh(chef); botSearchComplete = (bool)inventory.Catalog["complete"];
            if (player != 0) RefreshHuman(player);
        }
        private int humanFrame = -1, observedPlayer;
        private bool CanReachLanding(PlayerControls human, Vector3 floor)
        {
            int id = (int)human.PlayerIDProvider.GetID() + 1;
            RefreshHuman(id);
            NavigationActions observer;
            return observers.TryGetValue(id, out observer) && observer.CanReachFloor(floor);
        }
        private bool CanCollectLanding(PlayerControls human, GameObject item)
        { return HumanCanCollect(item, (int)human.PlayerIDProvider.GetID() + 1); }
        private void RefreshHuman(int player)
        {
            if (humanFrame == Time.frameCount && player == observedPlayer) return;
            humanFrame = Time.frameCount; observedPlayer = player;
            humanDistances.Clear(); humanSearchComplete = false; var human = Human(player); if (human == null) return;
            NavigationActions observer;
            if (!observers.TryGetValue(player, out observer))
            { observer = new NavigationActions(delegate(Vector3 d, int ms) { }, delegate { }); observers.Add(player, observer); }
            var catalog = (Json.Record)observer.Available(human);
            humanSearchComplete = (bool)catalog["complete"];
            foreach (Json.Record identity in (List<object>)catalog["destinations"])
            {
                int instance = (int)identity["objectInstanceId"]; humanDistances[instance] = (float)identity["routeDistance"];
            }
            foreach (GameObject go in navigation.Targets())
            {
                if (!Counter(go)) continue;
                string key = player + ":" + go.GetInstanceID(); CounterMotion track;
                if (!counterMotion.TryGetValue(key, out track)) { track = new CounterMotion(); counterMotion.Add(key, track); }
                track.Observe(go.transform.position, Time.realtimeSinceStartup, HumanDistance(go) != Single.MaxValue);
            }
        }
        private float HumanDistance(GameObject go)
        { float d; return go != null && humanDistances.TryGetValue(go.GetInstanceID(), out d) ? d : Single.MaxValue; }
        private bool Reachable(GameObject go) { return go != null && inventory.DistanceTo(go) != Single.MaxValue; }
        private float ApproachCost(GameObject target)
        {
            float distance = inventory.DistanceTo(target);
            if (distance != Single.MaxValue) return distance;
            // Moving kitchens can invalidate the idle catalogue before its
            // frontier reaches a local station. Let the active child planner
            // prove the route on our current support before any interaction.
            return !(bool)inventory.Catalog["complete"] && navigation.SharesCurrentSupport(target)
                ? 20000 + Vector3.Distance(chef.transform.position, target.transform.position) : Single.MaxValue;
        }
        private TaskStep WalkTo(GameObject aim, Vector3 stance, bool alternatives = false, Func<bool> obsolete = null)
        {
            bool started = false; float retry = 0, planningSince = -1;
            Vector3 offset = aim == null ? Vector3.zero : stance - aim.transform.position;
            var step = new TaskStep { Phase = "approaching" };
            step.Progress = delegate {
                if (obsolete != null && obsolete())
                {
                    if (started && navigation.Action.State == "navigating") navigation.Stop("handoff_cargo_changed");
                    return true;
                }
                if (navigation.Action.State == "navigating") return false;
                if (started && (navigation.Action.Reason == "arrived" || navigation.Action.Reason == "positioned_no_action")) { step.Succeeded = true; return true; }
                if (started && alternatives && navigation.Action.Reason != "planning_in_progress" && navigation.Action.Reason != "planning_budget_exceeded") return true;
                input(0); if (Time.realtimeSinceStartup < retry) return false; retry = Time.realtimeSinceStartup + .2f;
                // A fixed world-space point can slide off a moving raft before
                // the route is certified. Track the receiving chef until the
                // navigator takes over and pins the point to its native floor.
                if (alternatives && aim == null) return true;
                string error = navigation.StartPosition(chef, Run.Owner, aim, alternatives ? aim.transform.position + offset : stance, true);
                started = error == null;
                if (started || !alternatives) { planningSince = -1; return false; }
                if (planningSince < 0) planningSince = Time.realtimeSinceStartup;
                return error != "planning_in_progress" && error != "planning_budget_exceeded" ||
                    Time.realtimeSinceStartup - planningSince >= 2;
            }; return step;
        }
        private static bool Counter(GameObject go)
        {
            return go != null && go.GetComponent<ServerAttachStation>() != null && !TaskNative.Equipment(go) &&
                go.GetComponent<ServerWorkstation>() == null && go.GetComponent<ServerWashingStation>() == null &&
                go.GetComponent<ServerRubbishBin>() == null && go.GetComponent<PickupItemSpawner>() == null && go.GetComponent<ServerPlateStation>() == null;
        }
        private bool CanDeliver(GameObject item, GameObject target, bool supplyPrefab = false)
        { string op; return LogisticsChecks.Delivery(chef, "stage_item", item, target, out op, supplyPrefab) == null; }
        // A continuously moving support can invalidate the catalogue frontier.
        // Prove one local interaction stance with the same native walk checks.
        private float StorageDistance(GameObject target, float maximum)
        {
            float distance = inventory.DistanceTo(target);
            if (distance == Single.MaxValue && !botSearchComplete && Run.Running && navigation.SharesCurrentSupport(target))
                distance = navigation.LocalApproachDistance(target, maximum);
            return distance;
        }
        private GameObject Parking(GameObject item, GameObject preferred = null, bool burners = false, HashSet<GameObject> excluded = null, float maxDistance = Single.MaxValue, bool probeUnknown = false)
        {
            RefreshWorld(); GameObject best = null; float cost = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if (excluded != null && excluded.Contains(target)) continue;
                if ((!Counter(target) && !(burners && target.GetComponent<CookingStation>() != null)) ||
                    ActionObservations.Occupant(target) != null || !CanDeliver(item, target)) continue;
                // A conveyor is a transport, not stable storage for a hot pot.
                if (TaskNative.Component<ServerConveyorStation>(target) != null) continue;
                float distance = StorageDistance(target, maxDistance);
                bool provisional = distance == Single.MaxValue && probeUnknown && !botSearchComplete;
                if (provisional) distance = Vector3.Distance(chef.transform.position, target.transform.position);
                if (distance == Single.MaxValue || distance > maxDistance) continue;
                float score = distance + (provisional ? 20000 : 0) + (target == preferred ? 0 : burners && target.GetComponent<CookingStation>() != null ? 1000 : 2000);
                if (score < cost) { cost = score; best = target; }
            }
            if (burners && best != preferred && !botSearchComplete) return null;
            return best;
        }
        private IEnumerable<TaskStep> Park(GameObject item, GameObject preferred = null, bool burners = false, GameObject reserved = null)
        {
            var excluded = new HashSet<GameObject>(); float rejectedAt = 0;
            if (reserved != null) excluded.Add(reserved);
            while (true)
            {
                GameObject destination = null;
                yield return Until("waiting_for_safe_counter", delegate {
                    destination = Parking(item, preferred, burners, excluded, probeUnknown: assembly != null && AssemblyHeatDue());
                    if (destination == null && Time.realtimeSinceStartup - rejectedAt > 5)
                        excluded.RemoveWhere(target => target != reserved);
                    return destination != null;
                });
                TaskStep placement = Do("place_at", destination); placement.ReplanOnBlock = true;
                yield return placement;
                if (placement.Succeeded) yield break;
                excluded.Add(destination); rejectedAt = Time.realtimeSinceStartup;
            }
        }
        // A loose object the native selection prefers over a station blocks it
        // as surely as an occupant does. Set cargo down, then move that object.
        private IEnumerable<TaskStep> Clear(GameObject obstruction)
        {
            if (obstruction == null) yield break;
            GameObject held = ActionObservations.Held(chef);
            if (held != null) foreach (TaskStep park in Park(held)) yield return park;
            if (!obstruction.activeInHierarchy || !ActionObservations.Loose(obstruction)) yield break;
            TaskStep pickup = Do("pick_up", obstruction); pickup.ReplanOnBlock = true;
            yield return pickup;
            if (pickup.Succeeded) foreach (TaskStep park in Park(obstruction)) yield return park;
        }
        // A moving raft can continually invalidate a distant catalogue frontier.
        // Let the pickup's bounded active planner try a local source on the
        // same native support; this is not a route proof.
        private bool Obtainable(LogisticsItem entry, string operation)
        {
            string collect = LogisticsChecks.Collect(chef, entry, operation, true);
            return collect == null || collect == "source_unreachable" && ApproachCost(entry.Source) != Single.MaxValue && LogisticsChecks.CanCollectEmpty(chef, entry.Source);
        }
    }
}
