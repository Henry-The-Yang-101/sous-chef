using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        // Prerequisites use the same small, interruptible scheduler steps as
        // recipe work, so washing/clearing a board cannot hide a hot batch.
        private AssemblyJob SelectAssemblyWashing()
        {
            if (!assembly.Automatic || assembly.Base != null) return null;
            foreach (var sink in navigation.Targets())
            {
                var washing = sink.GetComponent<ServerWashingStation>();
                if (washing == null || AssemblyDeferred(sink) || ApproachCost(sink) == Single.MaxValue) continue;
                var rack = ActionObservations.DryingStation(washing);
                if (rack == null) continue;
                var obstruction = SinkObstruction(sink, rack.gameObject);
                if (obstruction != null)
                    return AssemblyClearSurface(obstruction);
                if (ActionObservations.DirtyCount(sink) > 0)
                    return new AssemblyJob { Stage = assembly.Root, Destination = sink, Operation = "work_at", Utility = "wash_plate" };
                foreach (var entry in inventory.Items)
                {
                    if (entry.Supply || !TaskNative.Dirty(entry.Item) || AssemblyDeferred(entry.Item) || !AssemblyObtainable(entry, "move_item", true)) continue;
                    bool held = ActionObservations.Held(chef) == entry.Item;
                    if (!CanDeliver(entry.Item, sink)) continue;
                    return new AssemblyJob { Stage = assembly.Root, Source = entry.Item,
                        Destination = held ? sink : entry.Source, Operation = held ? "place_at" : "pick_up", Utility = "wash_plate" };
                }
            }
            assembly.Waiting = "waiting_for_clean_plate_or_washing";
            return null;
        }

        private AssemblyJob AssemblyClearSurface(GameObject surface)
        {
            var item = ActionObservations.Occupant(surface);
            if (item == null || AssemblyDeferred(surface) || AssemblyDeferred(item)) return null;
            var entry = AssemblyEntry(item);
            if (entry == null || !AssemblyObtainable(entry, "move_item", true) || AssemblyParking(item, true) == null) return null;
            return new AssemblyJob { Stage = assembly.Root, Source = item, Destination = surface, Operation = "pick_up", Utility = "clear_workspace" };
        }

        private IEnumerable<TaskStep> ExecuteAssemblyAccess(AssemblyJob job)
        {
            var source = job.Source; var target = job.Destination;
            bool work = job.Operation == "work_at";
            Func<bool> valid = () => work ? ActionObservations.DirtyCount(target) > 0 &&
                SinkObstruction(target, ActionObservations.DryingStation(target.GetComponent<ServerWashingStation>())?.gameObject) == null :
                source != null && (job.Operation == "pick_up" ? ActionObservations.Receiver(target) == source :
                    ActionObservations.Held(chef) == source && TaskNative.Dirty(source) && CanDeliver(source, target));
            var next = AssemblyStep(job.Operation, target, valid, work ? "one_plate_clean" : null);
            next.FreeHands = ClearAssemblyHands;
            var authorize = next.BeforePress;
            next.BeforePress = () => authorize() && !AssemblyHeatDue(true) && AssemblyTripDeadline(job) == null;
            var abandon = next.AbandonBeforePress; float planningSince = -1; bool stalled = false; Vector3 planningPosition = Vector3.zero; Collider planningSupport = null;
            next.AbandonBeforePress = () => abandon() || AssemblyHeatDue(true) || AssemblyTripDeadline(job) != null || (stalled = AssemblyPlanningStalled(ref planningSince, ref planningPosition, ref planningSupport));
            assembly.Record(job.Utility, job.Stage, source, target);
            yield return next;
            if (!next.Succeeded)
            {
                var key = source ?? target;
                if (next.RouteFailed || stalled) DeferAssemblyRoute(key, target);
                else assembly.RetryAt[key] = Time.realtimeSinceStartup + .2f;
            }
            else if (job.Utility == "clear_workspace" && ActionObservations.Held(chef) == source)
                foreach (var step in ClearAssemblyHands(source)) yield return step;
        }
    }
}
