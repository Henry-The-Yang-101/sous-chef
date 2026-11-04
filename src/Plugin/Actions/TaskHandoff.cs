using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class Handoff
        {
            public GameObject Target;
            public bool Throw, Transport;
            public int Player;
            public string Mode;
            public Vector3? Approach;
        }
        private object handoffEvidence;
        private bool ThrowPossible(GameObject item, GameObject target)
        {
            // Reuse native arc/throwability and supported release-stance search.
            var planner = new ThrowPlanner(); ThrowPlan proof;
            return planner.Prepare(chef, item, target) == null && planner.Find(navigation, out proof) == null;
        }
        private Handoff FindHandoff(GameObject item, int player, List<Vector3> excluded)
        {
            RefreshWorld(player); PlayerControls human = Human(player); if (human == null || item == null) return null;
            GameObject best = null; bool throwItem = false; float score = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if (!Counter(target) || HumanDistance(target) > 5 || TaskNative.Component(target, "ServerConveyorStation") != null) continue;
                bool walk = Reachable(target) && CanDeliver(item, target);
                if (!walk && !ThrowPossible(item, target)) continue;
                float candidate = HumanDistance(target) * 100 + (walk ? inventory.DistanceTo(target) : 50);
                if (candidate < score) { best = target; throwItem = !walk; score = candidate; }
            }
            if (best != null) return new Handoff { Target = best, Throw = throwItem, Mode = throwItem ? "throw_near_player" : "counter_near_player" };
            DropPlan drop;
            if (new DropHandoff().Plan(chef, item, human, out drop) || ThrowPossible(item, human.gameObject))
                return new Handoff { Player = player, Mode = "at_player" };
            navigation.PrepareStances(chef);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6; Vector3 stance;
                Vector3 near = human.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 1.15f;
                bool skip = false; foreach (Vector3 tried in excluded) if (Vector3.Distance(tried, near) < .4f) skip = true;
                if (skip) continue;
                if (navigation.TryStance(near, out stance) && navigation.RouteCost(stance) != Single.MaxValue)
                    return new Handoff { Player = player, Mode = "drop_at_player", Approach = stance };
            }
            // Native next-receiver links include direction and rotated conveyors;
            // reverse belts and cycles never masquerade as a delivery path.
            foreach (GameObject target in navigation.Targets())
            {
                if (!Reachable(target) || !CanDeliver(item, target)) continue;
                if (ConveyorReachesHuman(target, item) || ReturningCounter(target, player))
                    return new Handoff { Target = target, Transport = true, Mode = "transport_to_player" };
            }
            return null;
        }
        private bool ReturningCounter(GameObject target, int player)
        {
            Vector3 visited;
            // Observe an actual human-accessible stop in this round. Unknown
            // motion is never treated as a promise about a future counter pose.
            return Counter(target) && humanCounterVisits.TryGetValue(player + ":" + target.GetInstanceID(), out visited) &&
                Vector3.Distance(visited, target.transform.position) > .5f;
        }
        private bool ConveyorReachesHuman(GameObject start, GameObject item)
        {
            var seen = new HashSet<int>(); GameObject current = start;
            for (int hop = 0; current != null && hop < 64 && seen.Add(current.GetInstanceID()); hop++)
            {
                if (current != start && HumanDistance(current) != Single.MaxValue) return true;
                Component belt = TaskNative.Component(current, "ServerConveyorStation"); if (belt == null) return false;
                object next = TaskNative.Call(belt, "GetAdjacentReceiver"); var component = next as Component;
                if (component == null) return false;
                var attachment = GameObjectUtils.RequestInterface<IAttachment>(item);
                if (attachment == null || !(bool)TaskNative.Call(next, "CanConveyTo", attachment)) return false;
                current = component.gameObject;
            }
            return false;
        }
        private bool HumanCanCollect(GameObject item, int player)
        {
            RefreshHuman(player); var human = Human(player);
            if (human != null && System.Object.ReferenceEquals(ActionObservations.Held(human), item)) return true;
            foreach (GameObject target in navigation.Targets())
                if (HumanDistance(target) != Single.MaxValue && System.Object.ReferenceEquals(ActionObservations.Occupant(target), item)) return true;
            return false;
        }
        private IEnumerable<TaskStep> Give(GameObject item, int player = 1)
        {
            var excluded = new List<Vector3>();
            while (true)
            {
                Handoff choice = null;
                yield return Until("waiting_for_player_handoff", delegate { choice = FindHandoff(item, player, excluded); return choice != null; });
                handoffEvidence = Json.Object("mode", choice.Mode, "itemInstanceId", item.GetInstanceID(), "player", player,
                    "targetInstanceId", choice.Target == null ? null : (object)choice.Target.GetInstanceID());
                TaskStep delivery;
                if (choice.Player != 0)
                {
                    if (choice.Approach.HasValue)
                    {
                        yield return WalkTo(Human(player).gameObject, choice.Approach.Value);
                        float settle = Time.realtimeSinceStartup + .3f;
                        yield return Until("settling_for_handoff", delegate { return Time.realtimeSinceStartup >= settle; });
                        DropPlan proof;
                        if (!new DropHandoff().Plan(chef, item, Human(player), out proof)) { excluded.Add(choice.Approach.Value); continue; }
                    }
                    delivery = new TaskStep { Phase = "giving_to_player", Command = delegate { return new ControlCommand { Operation = "throw_to_player", Destination = player.ToString() }; } };
                }
                else delivery = Do(choice.Throw ? "throw_to" : "place_at", choice.Target);
                delivery.ReplanOnBlock = true; yield return delivery;
                if (!delivery.Succeeded) continue;
                if (choice.Transport) yield return Until("waiting_for_transport_delivery", delegate { return HumanCanCollect(item, player); });
                yield break;
            }
        }
    }
}
