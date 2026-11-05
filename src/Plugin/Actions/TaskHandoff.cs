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
            public bool Throw, Transport, Ground;
            public int Player;
            public string Mode;
            public Vector3? Approach;
            public float? ArrivalSeconds;
        }
        private object handoffEvidence;
        private readonly ThrowOptions handoffThrows;
        private bool ThrowPossible(GameObject item, GameObject target, bool ground = false)
        {
            object description; return handoffThrows.Check(target, out description, ground);
        }
        private Handoff FindHandoff(GameObject item, int player, List<Vector3> excluded, HashSet<GameObject> blockedTargets)
        {
            if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) throw new InvalidOperationException("task_cargo_changed");
            handoffThrows.Begin(chef);
            // Rank usable choices. An unfinished counter proof must not hold a
            // feasible ground pass hostage on a continuously moving kitchen.
            try { return FindHandoffCore(item, player, excluded, blockedTargets); }
            finally { handoffThrows.End(); }
        }
        private Handoff FindHandoffCore(GameObject item, int player, List<Vector3> excluded, HashSet<GameObject> blockedTargets)
        {
            RefreshWorld(player); PlayerControls human = Human(player); if (human == null || item == null) return null;
            GameObject best = null; bool throwItem = false; float score = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if (blockedTargets.Contains(target) || !Counter(target) || ActionObservations.Occupant(target) != null ||
                    HumanDistance(target) > 5 || TaskNative.Component(target, "ServerConveyorStation") != null) continue;
                bool walk = Reachable(target) && CanDeliver(item, target);
                if (!walk && !ThrowPossible(item, target)) continue;
                float candidate = HumanDistance(target) * 100 + (walk ? inventory.DistanceTo(target) : 50);
                if (candidate < score) { best = target; throwItem = !walk; score = candidate; }
            }
            if (best != null) return new Handoff { Target = best, Throw = throwItem, Mode = throwItem ? "throw_near_player" : "counter_near_player" };
            DropPlan drop;
            if (new DropHandoff().Plan(chef, item, human, out drop) || ThrowPossible(item, human.gameObject))
                return new Handoff { Player = player, Mode = "at_player" };
            if (ThrowPossible(item, human.gameObject, true))
                return new Handoff { Player = player, Ground = true, Mode = "throw_ground_near_player" };
            navigation.PrepareStances(chef);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6; Vector3 stance;
                Vector3 near = human.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 1.45f;
                bool skip = false; foreach (Vector3 tried in excluded) if (Vector3.Distance(tried, near) < .4f) skip = true;
                if (skip) continue;
                if (navigation.TryStance(near, out stance) && navigation.RouteCost(stance) != Single.MaxValue)
                    return new Handoff { Player = player, Mode = "drop_at_player", Approach = stance };
            }
            // Native next-receiver links include direction and rotated conveyors;
            // reverse belts and cycles never masquerade as a delivery path.
            foreach (GameObject target in navigation.Targets())
            {
                if (blockedTargets.Contains(target) || !Reachable(target) || !CanDeliver(item, target)) continue;
                if (ConveyorReachesHuman(target, item)) return new Handoff { Target = target, Transport = true, Mode = "conveyor_to_player" };
                float seconds = CounterArrival(target, player);
                if (seconds != Single.MaxValue) return new Handoff { Target = target, Transport = true, Mode = "moving_counter_to_player", ArrivalSeconds = seconds };
            }
            return null;
        }
        private float CounterArrival(GameObject target, int player)
        {
            CounterMotion track;
            return Counter(target) && counterMotion.TryGetValue(player + ":" + target.GetInstanceID(), out track)
                ? track.ArrivalSeconds(Time.realtimeSinceStartup) : Single.MaxValue;
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
        private bool HumanCanCollect(GameObject item, int player, bool refresh = true, float maximumDistance = Single.MaxValue)
        {
            if (item == null) return false;
            if (refresh) RefreshHuman(player); var human = Human(player);
            if (human != null && System.Object.ReferenceEquals(ActionObservations.Held(human), item)) return true;
            if (human != null && HumanDistance(item) != Single.MaxValue && HumanDistance(item) <= maximumDistance && LogisticsChecks.CanCollectEmpty(human, item)) return true;
            foreach (GameObject target in navigation.Targets())
                if (HumanDistance(target) != Single.MaxValue && HumanDistance(target) <= maximumDistance && System.Object.ReferenceEquals(ActionObservations.Occupant(target), item)) return true;
            return false;
        }
        // With `lossTolerant`, a pass that leaves P2's hands without reaching the
        // human ends here undelivered (absent from deliveredItems) for the caller.
        private IEnumerable<TaskStep> Give(GameObject item, int player = 1, bool backgroundTransport = false, bool lossTolerant = false)
        {
            var excluded = new List<Vector3>();
            var blockedTargets = new HashSet<GameObject>();
            float rejectedAt = Time.realtimeSinceStartup;
            Action<Vector3> reject = delegate(Vector3 point) { excluded.Add(point); rejectedAt = Time.realtimeSinceStartup; };
            while (true)
            {
                Handoff choice = null;
                yield return Until("waiting_for_player_handoff", delegate {
                    choice = FindHandoff(item, player, excluded, blockedTargets);
                    if (choice == null && Time.realtimeSinceStartup - rejectedAt > 5) { excluded.Clear(); blockedTargets.Clear(); }
                    return choice != null;
                });
                handoffEvidence = Json.Object("mode", choice.Mode, "itemInstanceId", item.GetInstanceID(), "player", player,
                    "predictedArrivalSeconds", choice.ArrivalSeconds,
                    "targetInstanceId", choice.Target == null ? null : (object)choice.Target.GetInstanceID());
                TaskStep delivery;
                if (choice.Player != 0)
                {
                    if (choice.Approach.HasValue)
                    {
                        TaskStep approach = WalkTo(Human(player).gameObject, choice.Approach.Value, true);
                        yield return approach;
                        if (!approach.Succeeded) { reject(choice.Approach.Value); continue; }
                        float settle = Time.realtimeSinceStartup + .3f;
                        yield return Until("settling_for_handoff", delegate { return Time.realtimeSinceStartup >= settle; });
                        DropPlan proof;
                        if (!new DropHandoff().Plan(chef, item, Human(player), out proof)) { reject(choice.Approach.Value); continue; }
                    }
                    delivery = new TaskStep { Phase = "giving_to_player", Command = delegate { return new ControlCommand { Operation = "throw_to_player", Destination = player.ToString(), GroundHandoff = choice.Ground }; } };
                }
                else delivery = Do(choice.Throw ? "throw_to" : "place_at", choice.Target);
                // A bouncing raw ingredient can miss the primitive's precise
                // catch/drop window yet finish safely within the human's reach.
                // Reconcile the task's actual goal before declaring failure;
                // never replay an already released throw or replace its cargo.
                if (!choice.Transport) delivery.Outcome = delegate {
                    if (!HumanCanCollect(item, player, true, choice.Ground ? Single.MaxValue : 5)) return false;
                    ((Json.Record)handoffEvidence)["reconciledOutcome"] = child.Run.Reason; return true;
                };
                delivery.ReplanOnBlock = true; delivery.ReplanAfterPress = lossTolerant; yield return delivery;
                if (!delivery.Succeeded)
                {
                    if (lossTolerant && !System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) yield break;
                    if (choice.Target != null) blockedTargets.Add(choice.Target);
                    rejectedAt = Time.realtimeSinceStartup; continue;
                }
                if (choice.Transport)
                {
                    if (backgroundTransport) DispatchDelivery(item, player);
                    else yield return Until("waiting_for_transport_delivery", delegate { return HumanCanCollect(item, player); });
                }
                deliveredItems.Add(item);
                yield break;
            }
        }
    }
}
