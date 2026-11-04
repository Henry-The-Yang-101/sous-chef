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
        { if (go != null) foreach (Component c in go.GetComponents<Component>()) if (c != null && c.GetType().Name == name) return c; return null; }
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
        { string c = State(item), m = State(item, true); return c == "Completed" || c == "OverDoing" || m == "Completed" || m == "OverDoing"; }
        public static bool Burnt(GameObject item) { return State(item) == "Ruined" || State(item, true) == "Ruined"; }
        public static float Remaining(GameObject item, bool mixing = false)
        {
            Component h = Component(item, mixing ? "ServerMixingHandler" : "ServerCookingHandler");
            if (h == null) return Single.MaxValue;
            float progress = (float)Call(h, mixing ? "GetMixingProgress" : "GetCookingProgress");
            var property = h.GetType().GetProperty(mixing ? "AccessMixingTime" : "AccessCookingTime");
            return Math.Max(0, (float)property.GetValue(h, null) - progress);
        }
        public static bool Equipment(GameObject go)
        { return go != null && (go.GetComponent<CookingStation>() != null || Component(go, "MixingStation") != null || Component(go, "ServerCookingRegion") != null); }
        public static bool Dirty(GameObject go) { return Component(go, "ServerDirtyPlateStack") != null; }
    }
    internal sealed partial class TaskActions
    {
        private readonly Dictionary<int, NavigationActions> observers = new Dictionary<int, NavigationActions>();
        private readonly Dictionary<int, float> humanDistances = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> humanCounterVisits = new Dictionary<int, Vector3>();
        private PlayerControls Human(int player = 1)
        {
            PlayerControls found = null;
            foreach (PlayerControls p in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (p != chef && p.PlayerIDProvider != null && (int)p.PlayerIDProvider.GetID() + 1 == player)
                { if (found != null) return null; found = p; }
            return found;
        }
        private void RefreshWorld(int player = 1)
        {
            inventory.Refresh(chef); RefreshHuman(player);
        }
        private void RefreshHuman(int player)
        {
            humanDistances.Clear(); var human = Human(player); if (human == null) return;
            NavigationActions observer;
            if (!observers.TryGetValue(player, out observer))
            { observer = new NavigationActions(delegate(Vector3 d, int ms) { }, delegate { }); observers.Add(player, observer); }
            var catalog = (Json.Record)observer.Available(human);
            foreach (Json.Record identity in (List<object>)catalog["destinations"])
            {
                int instance = (int)identity["objectInstanceId"]; humanDistances[instance] = (float)identity["routeDistance"];
                GameObject go = observer.Resolve((string)identity["id"]);
                if (Counter(go)) humanCounterVisits[instance] = go.transform.position;
            }
        }
        private float HumanDistance(GameObject go)
        { float d; return go != null && humanDistances.TryGetValue(go.GetInstanceID(), out d) ? d : Single.MaxValue; }
        private bool Reachable(GameObject go) { return go != null && inventory.DistanceTo(go) != Single.MaxValue; }
        private TaskStep WalkTo(GameObject aim, Vector3 stance)
        {
            bool started = false; float retry = 0;
            return new TaskStep { Phase = "approaching", Progress = delegate {
                if (navigation.Action.State == "navigating") return false;
                if (started && (navigation.Action.Reason == "arrived" || navigation.Action.Reason == "positioned_no_action")) return true;
                input(0); if (Time.realtimeSinceStartup < retry) return false; retry = Time.realtimeSinceStartup + .2f;
                started = navigation.StartPosition(chef, Run.Owner, aim, stance, true) == null; return false;
            } };
        }
        private static bool Counter(GameObject go)
        {
            return go != null && go.GetComponent<ServerAttachStation>() != null && !TaskNative.Equipment(go) &&
                go.GetComponent<ServerWorkstation>() == null && go.GetComponent<ServerWashingStation>() == null &&
                go.GetComponent<ServerRubbishBin>() == null && go.GetComponent<PickupItemSpawner>() == null;
        }
        private bool CanDeliver(GameObject item, GameObject target)
        { string op; return LogisticsChecks.Delivery(chef, "stage_item", item, target, out op) == null; }
        private GameObject Parking(GameObject item, GameObject preferred = null, bool burners = false)
        {
            RefreshWorld(); GameObject best = null; float cost = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if ((!Counter(target) && !(burners && target.GetComponent<CookingStation>() != null)) || !Reachable(target) || !CanDeliver(item, target)) continue;
                // A conveyor is a transport, not stable storage for a hot pot.
                if (TaskNative.Component(target, "ServerConveyorStation") != null) continue;
                float score = inventory.DistanceTo(target) + (target == preferred ? 0 : burners && target.GetComponent<CookingStation>() != null ? 1000 : 2000);
                if (score < cost) { cost = score; best = target; }
            }
            return best;
        }
        private IEnumerable<TaskStep> Park(GameObject item, GameObject preferred = null, bool burners = false)
        {
            GameObject destination = null;
            yield return Until("waiting_for_safe_counter", delegate { destination = Parking(item, preferred, burners); return destination != null; });
            yield return Do("place_at", destination);
        }
        private LogisticsItem Source(string selector, bool utensil = false)
        {
            inventory.Refresh(chef); LogisticsItem best = null; float score = Single.MaxValue;
            foreach (LogisticsItem entry in inventory.Items)
            {
                bool match = utensil ? selector == "plate" ? entry.CleanPlate : entry.Empty && entry.UtensilType == LogisticsInventory.CanonicalUtensil(selector) : LogisticsInventory.IngredientMatches(entry, selector);
                if (!match || LogisticsChecks.Collect(chef, entry, selector == "plate" ? "get_plate" : "move_item") != null) continue;
                if (TaskNative.Equipment(entry.Source)) continue;
                float candidate = entry.Distance + (entry.Supply ? 10000 : 0);
                if (candidate < score) { best = entry; score = candidate; }
            }
            return best;
        }
    }
}
