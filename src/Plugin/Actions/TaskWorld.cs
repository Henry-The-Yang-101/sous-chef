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
        public static Component Component(GameObject go, string name)
        {
            if (go != null) foreach (Component c in go.GetComponents<Component>())
                if (c != null) for (Type type = c.GetType(); type != null; type = type.BaseType) if (type.Name == name) return c;
            return null;
        }
        public static object Call(object instance, string method, params object[] args)
        {
            if (instance == null) return null;
            var m = instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new InvalidOperationException("native_method_unavailable_" + method);
            return m.Invoke(instance, args);
        }
        public static string State(GameObject item, bool mixing = false)
        {
            Component handler = Component(item, mixing ? "ServerMixingHandler" : "ServerCookingHandler");
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
        public static bool IsMixer(GameObject target) { return Component(StationFor(target), "MixingStation") != null; }
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
            Component h = Component(item, mixing ? "ServerMixingHandler" : "ServerCookingHandler");
            if (h == null) return Single.MaxValue;
            float progress = (float)Call(h, mixing ? "GetMixingProgress" : "GetCookingProgress");
            var property = h.GetType().GetProperty(mixing ? "AccessMixingTime" : "AccessCookingTime");
            return Math.Max(0, (float)property.GetValue(h, null) * (untilRuined ? 2 : 1) - progress);
        }
        public static bool Equipment(GameObject go)
        { return go != null && (go.GetComponent<CookingStation>() != null || Component(go, "MixingStation") != null || Component(go, "HeatedCookingStation") != null || Component(go, "ServerCookingRegion") != null); }
        public static bool Dirty(GameObject go) { return Component(go, "ServerDirtyPlateStack") != null; }
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
        private void RefreshHuman(int player)
        {
            humanDistances.Clear(); humanSearchComplete = false; var human = Human(player); if (human == null) return;
            NavigationActions observer;
            if (!observers.TryGetValue(player, out observer))
            { observer = new NavigationActions(delegate(Vector3 d, int ms) { }, delegate { }); observers.Add(player, observer); }
            var catalog = (Json.Record)observer.Available(human);
            humanSearchComplete = (bool)catalog["complete"];
            foreach (Json.Record identity in (List<object>)catalog["destinations"])
            {
                int instance = (int)identity["objectInstanceId"]; humanDistances[instance] = (float)identity["routeDistance"];
                GameObject go = observer.Resolve((string)identity["id"]);
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
        private TaskStep WalkTo(GameObject aim, Vector3 stance, bool alternatives = false)
        {
            bool started = false; float retry = 0;
            var step = new TaskStep { Phase = "approaching" };
            step.Progress = delegate {
                if (navigation.Action.State == "navigating") return false;
                if (started && (navigation.Action.Reason == "arrived" || navigation.Action.Reason == "positioned_no_action")) { step.Succeeded = true; return true; }
                if (started && alternatives && navigation.Action.Reason != "planning_in_progress" && navigation.Action.Reason != "planning_budget_exceeded") return true;
                input(0); if (Time.realtimeSinceStartup < retry) return false; retry = Time.realtimeSinceStartup + .2f;
                started = navigation.StartPosition(chef, Run.Owner, aim, stance, true) == null; return false;
            }; return step;
        }
        private static bool Counter(GameObject go)
        {
            return go != null && go.GetComponent<ServerAttachStation>() != null && !TaskNative.Equipment(go) &&
                go.GetComponent<ServerWorkstation>() == null && go.GetComponent<ServerWashingStation>() == null &&
                go.GetComponent<ServerRubbishBin>() == null && go.GetComponent<PickupItemSpawner>() == null;
        }
        private bool CanDeliver(GameObject item, GameObject target)
        { string op; return LogisticsChecks.Delivery(chef, "stage_item", item, target, out op) == null; }
        private GameObject Parking(GameObject item, GameObject preferred = null, bool burners = false, HashSet<GameObject> excluded = null)
        {
            RefreshWorld(); GameObject best = null; float cost = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if (excluded != null && excluded.Contains(target)) continue;
                if ((!Counter(target) && !(burners && target.GetComponent<CookingStation>() != null)) ||
                    ActionObservations.Occupant(target) != null || !Reachable(target) || !CanDeliver(item, target)) continue;
                // A conveyor is a transport, not stable storage for a hot pot.
                if (TaskNative.Component(target, "ServerConveyorStation") != null) continue;
                float score = inventory.DistanceTo(target) + (target == preferred ? 0 : burners && target.GetComponent<CookingStation>() != null ? 1000 : 2000);
                if (score < cost) { cost = score; best = target; }
            }
            if (burners && best != preferred && !botSearchComplete) return null;
            return best;
        }
        private IEnumerable<TaskStep> Park(GameObject item, GameObject preferred = null, bool burners = false)
        {
            var excluded = new HashSet<GameObject>(); float rejectedAt = 0;
            while (true)
            {
                GameObject destination = null;
                yield return Until("waiting_for_safe_counter", delegate {
                    destination = Parking(item, preferred, burners, excluded);
                    if (destination == null && Time.realtimeSinceStartup - rejectedAt > 5) excluded.Clear();
                    return destination != null;
                });
                TaskStep placement = Do("place_at", destination); placement.ReplanOnBlock = true;
                yield return placement;
                if (placement.Succeeded) yield break;
                excluded.Add(destination); rejectedAt = Time.realtimeSinceStartup;
            }
        }
        private LogisticsItem Source(string selector, bool utensil = false)
        {
            inventory.Refresh(chef); LogisticsItem best = null; float score = Single.MaxValue;
            string utensilType = LogisticsInventory.CanonicalUtensil(selector); bool plate = utensilType == "plate";
            foreach (LogisticsItem entry in inventory.Items)
            {
                if (!entry.Supply && deliveredItems.Contains(entry.Item)) continue;
                bool match = utensil ? plate ? entry.CleanPlate : entry.Empty && entry.UtensilType == utensilType : LogisticsInventory.IngredientMatches(entry, selector);
                if (!match) continue;
                string collect = LogisticsChecks.Collect(chef, entry, plate ? "get_plate" : "move_item");
                float approach = ApproachCost(entry.Source);
                bool provisional = collect == "source_unreachable" && approach != Single.MaxValue && LogisticsChecks.CanCollectEmpty(chef, entry.Source);
                if (collect != null && !provisional) continue;
                if (!utensil && TaskNative.Equipment(entry.Holder ?? entry.Source)) continue;
                // A moving raft can continually invalidate a distant catalogue
                // frontier. Let the pickup's bounded active planner try a local
                // source on the same native support; this is not a route proof.
                // Certified sources always win over these provisional choices.
                float candidate = (provisional ? approach : entry.Distance) + (entry.Supply ? 10000 : 0);
                if (candidate < score) { best = entry; score = candidate; }
            }
            return best;
        }
    }
}
