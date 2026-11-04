using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Separate planners preview the same native arcs as execution. They only
    // query geometry/predicates; no input, lease, cargo or chef state is changed.
    internal sealed class ThrowOptions
    {
        private sealed class Option
        {
            public GameObject Target, Occupant;
            public Vector3 Position, Forward;
            public readonly ThrowPlanner Planner = new ThrowPlanner();
            public ThrowPlan Plan;
            public string Reason = ThrowPlanner.Pending;
            public float RetryAt;
            public bool Seen;
            public bool Ground;
            public Collider Support;
            public long LastSlice;
        }
        private readonly NavigationActions navigation;
        private readonly DropHandoff drops = new DropHandoff();
        private readonly Dictionary<long, Option> options = new Dictionary<long, Option>();
        private PlayerControls chef;
        private GameObject item;
        private Vector3 mount;
        private float requestedAt;
        private long slices;
        private bool observing, observeWinds;
        private WindVolume[] observedWinds;
        private WindVolume[] Winds()
        {
            if (!observing || !observeWinds) return null;
            if (observedWinds == null) observedWinds = UnityEngine.Object.FindObjectsOfType<WindVolume>();
            return observedWinds;
        }
        public void EndObservation() { observing = false; observedWinds = null; }

        public ThrowOptions(NavigationActions navigation) { this.navigation = navigation; }
        public object Describe()
        {
            var result = new List<object>();
            foreach (Option option in options.Values)
                result.Add(Json.Object("targetInstanceId", option.Target == null ? 0 : option.Target.GetInstanceID(),
                    "ground", option.Ground, "reason", option.Reason, "slices", option.LastSlice, "diagnostics", option.Planner.Diagnostics));
            return result;
        }
        public void Reset()
        {
            foreach (Option option in options.Values) option.Planner.CancelPlanning();
            options.Clear(); chef = null; item = null; EndObservation();
        }
        private bool SameActor(PlayerControls actor)
        {
            // Translation (including raft motion) does not change held geometry.
            // Keep bounded candidate progress; Check revalidates every positive
            // proof with current launch physics, obstacles and walking routes.
            return actor != null && actor == chef && item != null && item == ActionObservations.Held(actor) &&
                Vector3.Distance(mount, actor.transform.InverseTransformPoint(item.transform.position)) < .02f;
        }
        public void Begin(PlayerControls actor)
        {
            if (!SameActor(actor))
            {
                Reset(); chef = actor; item = actor == null ? null : ActionObservations.Held(actor);
                if (item != null) mount = actor.transform.InverseTransformPoint(item.transform.position);
            }
            observing = true; observedWinds = null;
            observeWinds = item != null && item.GetComponent<AttachmentWindReceiver>() != null && item.GetComponent<ServerAttachmentWindReceiver>() != null;
            requestedAt = Time.realtimeSinceStartup;
            foreach (Option option in options.Values) option.Seen = false;
        }
        private static bool SameTarget(Option option)
        {
            if (option.Ground && option.Support != null)
            {
                var player = option.Target == null ? null : option.Target.GetComponent<PlayerControls>();
                return player != null && player.gameObject.activeInHierarchy && player.GroundCollider == option.Support &&
                    Vector3.Distance(option.Position, option.Support.transform.InverseTransformPoint(player.transform.position)) < .2f;
            }
            return option.Target != null && option.Target.activeInHierarchy &&
                Vector3.Distance(option.Position, option.Target.transform.position) < .02f &&
                Vector3.Angle(option.Forward, option.Target.transform.forward) < .5f &&
                option.Occupant == Occupant(option.Target);
        }
        private static GameObject Occupant(GameObject target)
        { var player = target.GetComponent<PlayerControls>(); return player == null ? ActionObservations.Occupant(target) : ActionObservations.Held(player); }
        private void Prepare(Option option)
        {
            option.Planner.CancelPlanning(); option.Plan = null;
            if (option.Target == null || !option.Target.activeInHierarchy)
            { option.Reason = "target_unavailable"; option.RetryAt = Time.realtimeSinceStartup + 2; return; }
            option.Position = option.Target.transform.position; option.Forward = option.Target.transform.forward;
            var player = option.Ground ? option.Target.GetComponent<PlayerControls>() : null;
            option.Support = player == null ? null : player.GroundCollider;
            if (option.Support != null) option.Position = option.Support.transform.InverseTransformPoint(option.Position);
            option.Occupant = Occupant(option.Target);
            option.Reason = option.Planner.Prepare(chef, item, option.Target, Winds(), option.Ground) ?? ThrowPlanner.Pending;
            option.RetryAt = Time.realtimeSinceStartup + 2;
        }
        public bool Check(GameObject target, out object description, bool ground = false)
        {
            description = Json.Object("state", "unavailable", "reason", "hands_empty");
            if (chef == null || item == null || target == null) return false;
            var player = target.GetComponent<PlayerControls>(); DropPlan drop;
            if (player != null && drops.Plan(chef, item, player, out drop))
            { description = Json.Object("state", "feasible", "plan", drop.Describe()); return true; }
            Option option;
            long key = ((long)target.GetInstanceID() << 1) | (ground ? 1L : 0L);
            if (!options.TryGetValue(key, out option))
            { option = new Option { Target = target, Ground = ground }; options[key] = option; Prepare(option); }
            else if (!SameTarget(option) || (option.Reason != null && option.Reason != ThrowPlanner.Pending && Time.realtimeSinceStartup >= option.RetryAt)) Prepare(option);
            option.Seen = true;
            if (option.Plan != null)
            {
                // Refresh positive proofs against current obstacles, height,
                // receiver availability, wind and the current walking graph.
                ThrowPlan checkedPlan = null; Vector3 stance; Collider support; float routeCost = Single.MaxValue;
                Vector3 foot = chef.transform.position; foot.y = chef.GetComponent<CapsuleCollider>().bounds.min.y;
                Vector3 planned = option.Plan.CurrentStance;
                float uncertainty = Vector3.Distance(planned, foot) < .02f ? 0 : ArrivalCheck.PositionTolerance + .02f;
                if (option.Planner.Prepare(chef, item, target, Winds(), option.Ground) != null ||
                    !option.Planner.RestoreGround(option.Plan) ||
                    !navigation.TryStance(planned, out stance, out support) || Vector3.Distance(stance, planned) > .02f ||
                    !option.Planner.From(stance, false, out checkedPlan, uncertainty) || (routeCost = navigation.RouteCost(stance)) == Single.MaxValue)
                    Prepare(option);
                else { checkedPlan.WalkDistance = routeCost; checkedPlan.AnchorStance(support); option.Plan = checkedPlan; }
            }
            description = Json.Object("state", option.Plan != null ? "feasible" : option.Reason == ThrowPlanner.Pending ? "pending" : "unavailable",
                "reason", option.Reason, "plan", option.Plan == null ? null : option.Plan.Describe());
            return option.Plan != null;
        }
        public bool End()
        {
            var removed = new List<long>(); bool complete = true;
            foreach (var pair in options)
                if (!pair.Value.Seen) { pair.Value.Planner.CancelPlanning(); removed.Add(pair.Key); }
                else if (pair.Value.Reason == ThrowPlanner.Pending) complete = false;
            foreach (long key in removed) options.Remove(key);
            EndObservation(); return complete;
        }
        public void Tick(PlayerControls actor)
        {
            if (!SameActor(actor)) { Reset(); return; }
            if (Time.realtimeSinceStartup - requestedAt > 2) return;
            Option next = null;
            foreach (Option option in options.Values)
            {
                if (option.Reason != ThrowPlanner.Pending) continue;
                if (!SameTarget(option)) Prepare(option);
                if (option.Reason != ThrowPlanner.Pending) continue;
                if (next == null || option.LastSlice < next.LastSlice) next = option;
            }
            if (next == null) return;
            next.LastSlice = ++slices;
            // Available() already refreshed the shared read-only walking graph.
            next.Reason = next.Planner.Find(navigation, out next.Plan, 4, true, false);
            if (next.Reason != null && next.Reason != ThrowPlanner.Pending) next.RetryAt = Time.realtimeSinceStartup + 2;
        }
    }
}
