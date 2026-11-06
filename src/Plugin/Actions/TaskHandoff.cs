using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
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
            public float Score, WalkDistance, Separation;
        }
        private readonly GroundPassAccess groundPass;
        private object handoffEvidence;
        private readonly ThrowOptions handoffThrows;
        private GameObject handoffPreviewItem;
        private float handoffPreviewUntil, handoffCheckAt;
        private Handoff FindHandoff(GameObject item, int player, List<Vector3> excluded, HashSet<GameObject> blockedTargets)
        {
            if (handoffPreviewItem != item)
            { handoffPreviewItem = item; handoffPreviewUntil = Time.realtimeSinceStartup + .4f; handoffCheckAt = 0; }
            // Geometry previews keep their existing per-frame slice; inventory
            // and positive-proof comparison need only run ten times per second.
            if (Time.realtimeSinceStartup < handoffCheckAt) return null;
            handoffCheckAt = Time.realtimeSinceStartup + .1f;
            handoffThrows.Begin(chef);
            // Rank usable choices. An unfinished counter proof must not hold a
            // feasible ground pass hostage on a continuously moving kitchen.
            try { return FindHandoffCore(item, player, excluded, blockedTargets); }
            finally { handoffThrows.End(); }
        }
        private Handoff FindHandoffCore(GameObject item, int player, List<Vector3> excluded, HashSet<GameObject> blockedTargets)
        {
            RefreshWorld(player); PlayerControls human = Human(player); if (human == null || item == null) return null;
            Handoff best = null;
            float speed = Math.Max(.1f, chef.Movement.RunSpeed), tile = HandoffPreference.TileSize(human);
            var counters = new List<Handoff>();
            foreach (GameObject target in navigation.Targets())
            {
                if (blockedTargets.Contains(target) || !Counter(target) || ActionObservations.Occupant(target) != null ||
                    TaskNative.Component<ServerConveyorStation>(target) != null) continue;
                float humanCost = HumanDistance(target);
                NavigationActions observer;
                if (humanCost == Single.MaxValue && !humanSearchComplete && Vector3.Distance(human.transform.position, target.transform.position) < 7 &&
                    observers.TryGetValue(player, out observer) && observer.SharesCurrentSupport(target))
                    humanCost = observer.LocalApproachDistance(target, 5);
                if (humanCost > 5) continue;
                float botCost = StorageDistance(target, Single.MaxValue);
                float separation = HandoffPreference.Distance(target.transform.position, human.transform.position);
                if (botCost < Single.MaxValue && CanDeliver(item, target))
                    PreferHandoff(new Handoff { Target = target, Mode = "counter_near_player", WalkDistance = botCost,
                        Separation = separation, ArrivalSeconds = botCost / speed + .25f,
                        Score = HandoffPreference.Score(botCost / speed + .25f, separation, tile) }, ref best);
                counters.Add(new Handoff { Target = target, Throw = true, Mode = "throw_near_player", Separation = separation,
                    Score = HandoffPreference.Score(.2f, separation, tile) });
            }
            bool pending = false;
            // Compare throwing even when walking is possible. Preview only a
            // bounded shortlist; the existing worker-backed planners advance in
            // small slices while this task waits with neutral input.
            ConsiderThrow(new Handoff { Player = player, Ground = true, Mode = "throw_ground_near_player" },
                human.gameObject, speed, tile, ref best, ref pending);
            DropPlan drop;
            if (new DropHandoff().Plan(chef, item, human, out drop))
            {
                float separation = HandoffPreference.Distance(drop.Landing, human.transform.position);
                PreferHandoff(new Handoff { Player = player, Mode = "at_player", Separation = separation,
                    ArrivalSeconds = drop.FlightSeconds + .25f,
                    Score = HandoffPreference.Score(drop.FlightSeconds + .25f, separation, tile) }, ref best);
            }
            else ConsiderThrow(new Handoff { Player = player, Mode = "at_player", Separation = 0 },
                human.gameObject, speed, tile, ref best, ref pending);
            counters.Sort((a, b) => a.Score.CompareTo(b.Score));
            for (int i = 0; i < Math.Min(4, counters.Count); i++)
                ConsiderThrow(counters[i], counters[i].Target, speed, tile, ref best, ref pending);
            // Allow a short comparison window, never an unbounded wait for an
            // unfinished throw proof when a usable handoff already exists.
            if (pending && Time.realtimeSinceStartup < handoffPreviewUntil) return null;
            if (best != null) return best;
            navigation.PrepareStances(chef);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6; Vector3 stance;
                Vector3 near = human.transform.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 1.45f;
                bool skip = false; foreach (Vector3 tried in excluded) if (Vector3.Distance(tried, near - human.transform.position) < .4f) skip = true;
                if (skip) continue;
                if (navigation.TryStance(near, out stance) && navigation.RouteCost(stance) != Single.MaxValue)
                    return new Handoff { Player = player, Mode = "drop_at_player", Approach = stance - human.transform.position };
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
        private static void PreferHandoff(Handoff candidate, ref Handoff best)
        { if (best == null || candidate.Score < best.Score) best = candidate; }
        private void ConsiderThrow(Handoff candidate, GameObject target, float speed, float tile, ref Handoff best, ref bool pending)
        {
            float lowerBound = candidate.Ground ? .2f : HandoffPreference.Score(.2f, candidate.Separation, tile);
            if (best != null && lowerBound >= best.Score) return;
            object description; ThrowPlan plan;
            bool feasible = handoffThrows.Check(target, out description, out plan, candidate.Ground);
            pending |= (string)((Json.Record)description)["state"] == "pending";
            if (!feasible || plan == null) return;
            candidate.WalkDistance = plan.WalkDistance;
            candidate.Separation = plan.Ground ? plan.LandingDistance : candidate.Separation;
            candidate.ArrivalSeconds = plan.WalkDistance / speed + .2f + plan.FlightSeconds;
            candidate.Score = HandoffPreference.Score(candidate.ArrivalSeconds.Value, candidate.Separation, tile);
            PreferHandoff(candidate, ref best);
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
                Component belt = TaskNative.Component<ServerConveyorStation>(current); if (belt == null) return false;
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
        private IEnumerable<TaskStep> Give(GameObject item, int player = 1, Action<GameObject, int> dispatch = null, bool lossTolerant = false, Func<bool> defer = null)
        {
            groundPass.Reset();
            Func<bool> lost = () => item == null || !System.Object.ReferenceEquals(ActionObservations.Held(chef), item);
            var excluded = new List<Vector3>();
            var blockedTargets = new HashSet<GameObject>();
            float rejectedAt = Time.realtimeSinceStartup;
            Action<Vector3> reject = delegate(Vector3 point) { excluded.Add(point); rejectedAt = Time.realtimeSinceStartup; };
            while (true)
            {
                Handoff choice = null;
                yield return Until("waiting_for_player_handoff", delegate {
                    if (lost()) return true;
                    choice = FindHandoff(item, player, excluded, blockedTargets);
                    if (choice == null && Time.realtimeSinceStartup - rejectedAt > 5) { excluded.Clear(); blockedTargets.Clear(); }
                    return choice != null || defer != null && defer();
                });
                if (choice == null)
                {
                    if (!lost() && defer != null && defer()) yield break;
                    if (HumanCanCollect(item, player)) { deliveredItems.Add(item); yield break; }
                    if (lossTolerant) yield break;
                    throw new InvalidOperationException("task_cargo_changed");
                }
                handoffEvidence = Json.Object("mode", choice.Mode, "itemInstanceId", item.GetInstanceID(), "player", player,
                    "predictedArrivalSeconds", choice.ArrivalSeconds,
                    "walkDistance", choice.WalkDistance, "playerSeparation", choice.Separation,
                    "targetInstanceId", choice.Target == null ? null : (object)choice.Target.GetInstanceID());
                TaskStep delivery;
                if (choice.Player != 0)
                {
                    if (choice.Approach.HasValue)
                    {
                        var receiver = Human(player);
                        if (receiver == null) continue;
                        TaskStep approach = WalkTo(receiver.gameObject, receiver.transform.position + choice.Approach.Value, true, lost);
                        yield return approach;
                        if (!approach.Succeeded) { if (defer != null && defer()) yield break; reject(choice.Approach.Value); continue; }
                        float settle = Time.realtimeSinceStartup + .3f;
                        yield return Until("settling_for_handoff", delegate { return Time.realtimeSinceStartup >= settle; });
                        if (lost()) continue;
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
                if (defer != null)
                {
                    // Two chefs can block a counter stance. On a raft the chef
                    // then moves in world space without getting closer. Give
                    // the unpressed walk a new destination after sustained lack
                    // of progress relative to the counter, even as it drifts.
                    float closest = Single.MaxValue, replanAt = Time.realtimeSinceStartup + 6;
                    delivery.AbandonBeforePress = delegate {
                        if (child.RouteUnavailable) return true;
                        if (choice.Target == null || choice.Throw || child.Run.Phase != "navigating") return false;
                        float distance = Vector3.Distance(chef.transform.position, choice.Target.transform.position);
                        if (distance + .15f < closest) { closest = distance; replanAt = Time.realtimeSinceStartup + 6; }
                        return Time.realtimeSinceStartup >= replanAt;
                    };
                }
                delivery.ReplanOnBlock = true; delivery.ReplanAfterPress = lossTolerant; yield return delivery;
                if (!delivery.Succeeded)
                {
                    if (lossTolerant && !System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) yield break;
                    if (defer != null && defer()) yield break;
                    if (choice.Target != null) blockedTargets.Add(choice.Target);
                    rejectedAt = Time.realtimeSinceStartup; continue;
                }
                if (choice.Transport)
                {
                    if (dispatch != null) dispatch(item, player);
                    else yield return Until("waiting_for_transport_delivery", delegate { return HumanCanCollect(item, player); });
                }
                deliveredItems.Add(item);
                yield break;
            }
        }
    }
}
