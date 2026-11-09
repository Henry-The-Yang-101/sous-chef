using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterWashing() { Register("wash_dishes", WashingSupported, WashDishes); }
        private bool WashingSupported()
        {
            // Dispatch owns the task even when plates or a route are pending.
            // WashDishes checks reachability and cargo before issuing inputs.
            foreach (GameObject target in navigation.Targets())
                if (target.GetComponent<ServerWashingStation>() != null) return true;
            foreach (LogisticsItem item in inventory.Items)
                if (TaskNative.Component<ServerWaterGunSpray>(item.Item) != null) return true;
            return false;
        }
        private List<LogisticsItem> DirtySources()
        {
            inventory.Refresh(chef); var sources = new List<LogisticsItem>();
            foreach (LogisticsItem item in inventory.Items)
                if (!item.Supply && TaskNative.Dirty(item.Item) && !item.InUse && Reachable(item.Source) && LogisticsChecks.CanCollectEmpty(chef, item.Source)) sources.Add(item);
            sources.Sort(delegate(LogisticsItem a, LogisticsItem b) { return a.Distance.CompareTo(b.Distance); });
            return sources;
        }
        private GameObject Sink(bool humanOnly = false)
        {
            RefreshWorld(humanOnly ? 1 : 0); GameObject chosen = null; float distance = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                if (target.GetComponent<ServerWashingStation>() == null) continue;
                if (humanOnly && Reachable(target)) return null;
                float cost = humanOnly ? HumanDistance(target) : inventory.DistanceTo(target);
                if (cost < distance) { chosen = target; distance = cost; }
            }
            if (humanOnly && !botSearchComplete) return null;
            return chosen;
        }
        private bool HasDirtyDishes(GameObject sink)
        {
            return ActionObservations.DirtyCount(sink) > 0 || TaskNative.Dirty(ActionObservations.Held(chef)) ||
                DirtySources().Count > 0;
        }
        private static GameObject SinkObstruction(GameObject sink, GameObject drying)
        {
            if (ActionObservations.Occupant(sink) != null) return sink;
            GameObject item = ActionObservations.Occupant(drying);
            // The drying rack's clean stack is the native output for washing.
            return item != null && TaskNative.Component<CleanPlateStack>(item) == null ? drying : null;
        }
        private IEnumerable<TaskStep> ClearSink(GameObject sink, GameObject drying)
        {
            while (true)
            {
                GameObject surface = SinkObstruction(sink, drying);
                if (surface == null) yield break;
                GameObject item = ActionObservations.Occupant(surface);
                TaskStep pickup = Do("pick_up", surface);
                pickup.ReplanOnBlock = true;
                pickup.FreeHands = cargo => Park(cargo, reserved: drying);
                pickup.BeforePress = () => System.Object.ReferenceEquals(ActionObservations.Occupant(surface), item);
                pickup.AbandonBeforePress = () => !pickup.BeforePress();
                yield return pickup;
                if (pickup.Succeeded && System.Object.ReferenceEquals(ActionObservations.Held(chef), item))
                    foreach (TaskStep park in Park(item, reserved: drying)) yield return park;
            }
        }
        private TaskStep UseClearSink(string operation, GameObject sink, GameObject drying, string condition = null)
        {
            TaskStep action = Do(operation, sink, condition);
            Func<bool> ready = action.Ready;
            action.BeforePress = () => SinkObstruction(sink, drying) == null;
            action.Ready = () => action.BeforePress() && ready();
            action.ReplanOnBlock = true;
            action.AbandonBeforePress = () => !action.BeforePress();
            return action;
        }
        private IEnumerable<TaskStep> CollectDirtyBatch(bool allowEmpty = false, GameObject reserved = null)
        {
            // Freeze currently obtainable stacks. Native placement combines the
            // entire carried stack into the next, then pickup takes the aggregate.
            // New arrivals belong to a later repetition, not an expanding batch.
            List<LogisticsItem> batch = null;
            yield return Until("waiting_for_dirty_dishes", delegate {
                batch = DirtySources(); return allowEmpty || batch.Count > 0 || TaskNative.Dirty(ActionObservations.Held(chef));
            });
            foreach (LogisticsItem source in batch)
            {
                while (!System.Object.ReferenceEquals(ActionObservations.Held(chef), source.Item))
                {
                    yield return Until("waiting_for_dirty_stack", delegate {
                        return source.Item != null && source.Source != null && source.Source.activeInHierarchy &&
                            (source.Item == source.Source || System.Object.ReferenceEquals(ActionObservations.Occupant(source.Source), source.Item));
                    });
                    GameObject held = ActionObservations.Held(chef);
                    if (held != null && !TaskNative.Dirty(held))
                        foreach (TaskStep park in Park(held, reserved: reserved)) yield return park;
                    if (ActionObservations.Held(chef) != null) yield return Do("place_at", source.Source);
                    TaskStep pickup = Do("pick_up", source.Source);
                    pickup.FreeHands = cargo => Park(cargo, reserved: reserved);
                    pickup.ReplanForHeld = TaskNative.Dirty; yield return pickup;
                    if (pickup.Succeeded) break;
                }
            }
        }
        private List<GameObject> WashableBatch(GameObject gun)
        {
            var batch = new List<GameObject>();
            foreach (LogisticsItem item in inventory.Items)
            {
                Vector3 stance;
                if (!item.InUse && TaskNative.Dirty(item.Item) && TaskNative.Component<ServerWashable>(item.Item) != null &&
                    SprayStance(gun, item.Item, out stance)) batch.Add(item.Item);
            }
            return batch;
        }
        private IEnumerable<TaskStep> WashWithGun(LogisticsItem source, List<GameObject> batch)
        {
            GameObject gun = source.Item;
            TaskStep pickup = Do("pick_up", source.Source);
            pickup.UseHeld = held => System.Object.ReferenceEquals(held, gun);
            yield return pickup;
            foreach (GameObject stack in batch)
            {
                if (stack == null) continue;
                yield return SprayUntil(gun, stack, "spray_washing", () => stack == null ||
                    !stack.activeInHierarchy || ActionObservations.StackCount(stack) == 0);
            }
            foreach (TaskStep park in Park(gun, inventory.Home(gun))) yield return park;
        }
        private IEnumerable<TaskStep> WashDishes()
        {
            while (MoreRequested)
            {
                foreach (TaskStep step in WashBatch()) yield return step;
                completed++;
            }
        }
        private IEnumerable<TaskStep> WashBatch()
        {
            GameObject sink = null, drying = null;
            LogisticsItem gun = null; List<GameObject> batch = null;
            yield return Until("waiting_for_wash_access", delegate {
                sink = Sink();
                if (sink != null)
                {
                    var rack = ActionObservations.DryingStation(sink.GetComponent<ServerWashingStation>());
                    drying = rack == null ? null : rack.gameObject;
                    return SinkObstruction(sink, drying) != null || HasDirtyDishes(sink);
                }
                gun = SprayTool<ServerWaterGunSpray>();
                return gun != null && (batch = WashableBatch(gun.Item)).Count != 0;
            });
            if (sink == null)
            {
                foreach (TaskStep wash in WashWithGun(gun, batch)) yield return wash;
                yield break;
            }
            bool collected = false;
            while (true)
            {
                // If a blocker arrived on the way back with dishes, clearing it
                // parks the carried batch on a counter. Collect it again.
                if (SinkObstruction(sink, drying) != null && TaskNative.Dirty(ActionObservations.Held(chef)))
                    collected = false;
                foreach (TaskStep step in ClearSink(sink, drying)) yield return step;
                if (!collected)
                {
                    if (!HasDirtyDishes(sink))
                    {
                        yield return Until("waiting_for_wash_access", () => SinkObstruction(sink, drying) != null || HasDirtyDishes(sink));
                        continue;
                    }
                    foreach (TaskStep step in CollectDirtyBatch(ActionObservations.DirtyCount(sink) > 0, drying)) yield return step;
                    collected = true;
                }
                GameObject held = ActionObservations.Held(chef);
                if (held != null && !TaskNative.Dirty(held))
                    foreach (TaskStep park in Park(held, reserved: drying)) yield return park;
                if (ActionObservations.Held(chef) != null)
                {
                    TaskStep placement = UseClearSink("place_at", sink, drying);
                    yield return placement;
                    if (!placement.Succeeded) continue;
                }
                if (ActionObservations.DirtyCount(sink) > 0)
                {
                    TaskStep work = UseClearSink("work_at", sink, drying, "sink_empty");
                    work.FreeHands = cargo => Park(cargo, reserved: drying);
                    work.ReplanForHeld = TaskNative.Dirty;
                    yield return work;
                    if (!work.Succeeded) continue;
                }
                yield break;
            }
        }
    }
}
