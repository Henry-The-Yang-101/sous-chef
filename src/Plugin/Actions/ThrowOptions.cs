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
            public long LastSlice;
        }
        private readonly NavigationActions navigation;
        private readonly DropHandoff drops = new DropHandoff();
        private readonly Dictionary<int, Option> options = new Dictionary<int, Option>();
        private PlayerControls chef;
        private GameObject item;
        private Vector3 position, forward, mount;
        private float requestedAt;
        private long slices;

        public ThrowOptions(NavigationActions navigation) { this.navigation = navigation; }
        public void Reset()
        {
            foreach (Option option in options.Values) option.Planner.CancelPlanning();
            options.Clear(); chef = null; item = null;
        }
        private bool SameActor(PlayerControls actor)
        {
            return actor != null && actor == chef && item != null && item == ActionObservations.Held(actor) &&
                Vector3.Distance(position, actor.transform.position) < .05f && Vector3.Angle(forward, actor.transform.forward) < 1 &&
                Vector3.Distance(mount, actor.transform.InverseTransformPoint(item.transform.position)) < .02f;
        }
        public void Begin(PlayerControls actor)
        {
            if (!SameActor(actor))
            {
                Reset(); chef = actor; item = actor == null ? null : ActionObservations.Held(actor);
                if (actor != null) { position = actor.transform.position; forward = actor.transform.forward; }
                if (item != null) mount = actor.transform.InverseTransformPoint(item.transform.position);
            }
            requestedAt = Time.realtimeSinceStartup;
            foreach (Option option in options.Values) option.Seen = false;
        }
        private static bool SameTarget(Option option)
        {
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
            option.Occupant = Occupant(option.Target);
            option.Reason = option.Planner.Prepare(chef, item, option.Target) ?? ThrowPlanner.Pending;
            option.RetryAt = Time.realtimeSinceStartup + 2;
        }
        public bool Check(GameObject target, out object description)
        {
            description = Json.Object("state", "unavailable", "reason", "hands_empty");
            if (chef == null || item == null || target == null) return false;
            var player = target.GetComponent<PlayerControls>(); DropPlan drop;
            if (player != null && drops.Plan(chef, item, player, out drop))
            { description = Json.Object("state", "feasible", "plan", drop.Describe()); return true; }
            Option option;
            if (!options.TryGetValue(target.GetInstanceID(), out option))
            { option = new Option { Target = target }; options[target.GetInstanceID()] = option; Prepare(option); }
            else if (!SameTarget(option) || (option.Reason != null && option.Reason != ThrowPlanner.Pending && Time.realtimeSinceStartup >= option.RetryAt)) Prepare(option);
            option.Seen = true;
            if (option.Plan != null)
            {
                // Refresh positive proofs against current obstacles, height,
                // receiver availability, wind and the current walking graph.
                ThrowPlan checkedPlan = null; Vector3 stance; float routeCost = Single.MaxValue;
                Vector3 foot = chef.transform.position; foot.y = chef.GetComponent<CapsuleCollider>().bounds.min.y;
                float uncertainty = Vector3.Distance(option.Plan.Stance, foot) < .02f ? 0 : ArrivalCheck.PositionTolerance + .02f;
                if (option.Planner.Prepare(chef, item, target) != null ||
                    !navigation.TryStance(option.Plan.Stance, out stance) || Vector3.Distance(stance, option.Plan.Stance) > .02f ||
                    !option.Planner.From(stance, false, out checkedPlan, uncertainty) || (routeCost = navigation.RouteCost(stance)) == Single.MaxValue)
                    Prepare(option);
                else { checkedPlan.WalkDistance = routeCost; option.Plan = checkedPlan; }
            }
            description = Json.Object("state", option.Plan != null ? "feasible" : option.Reason == ThrowPlanner.Pending ? "pending" : "unavailable",
                "reason", option.Reason, "plan", option.Plan == null ? null : option.Plan.Describe());
            return option.Plan != null;
        }
        public bool End()
        {
            var removed = new List<int>(); bool complete = true;
            foreach (var pair in options)
                if (!pair.Value.Seen) { pair.Value.Planner.CancelPlanning(); removed.Add(pair.Key); }
                else if (pair.Value.Reason == ThrowPlanner.Pending) complete = false;
            foreach (int key in removed) options.Remove(key);
            return complete;
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
