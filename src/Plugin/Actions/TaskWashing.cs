using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterWashing() { Register("wash_dishes", WashingAvailable, WashDishes); }
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
        private bool WashingAvailable()
        {
            GameObject held = ActionObservations.Held(chef);
            GameObject sink = Sink();
            if (sink != null) return TaskNative.Dirty(held) || DirtySources().Count > 0 || ActionObservations.DirtyCount(sink) > 0;
            LogisticsItem gun = SprayTool<ServerWaterGunSpray>();
            return gun != null && WashableBatch(gun.Item).Count != 0;
        }
        private IEnumerable<TaskStep> CollectDirtyBatch(bool allowEmpty = false)
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
                    if (held != null && !TaskNative.Dirty(held)) foreach (TaskStep park in Park(held)) yield return park;
                    if (ActionObservations.Held(chef) != null) yield return Do("place_at", source.Source);
                    TaskStep pickup = Do("pick_up", source.Source);
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
                yield return Until("waiting_for_wash_access", WashingAvailable);
                foreach (TaskStep step in WashBatch()) yield return step;
                completed++;
            }
        }
        private IEnumerable<TaskStep> WashBatch()
        {
            GameObject sink = null;
            LogisticsItem gun = null; List<GameObject> batch = null;
            yield return Until("waiting_for_wash_access", delegate {
                sink = Sink(); if (sink != null) return true;
                gun = SprayTool<ServerWaterGunSpray>();
                return gun != null && (batch = WashableBatch(gun.Item)).Count != 0;
            });
            if (sink == null)
            {
                foreach (TaskStep wash in WashWithGun(gun, batch)) yield return wash;
                yield break;
            }
            foreach (TaskStep step in CollectDirtyBatch(ActionObservations.DirtyCount(sink) > 0)) yield return step;
            GameObject held = ActionObservations.Held(chef);
            if (held != null && !TaskNative.Dirty(held)) foreach (TaskStep park in Park(held)) yield return park;
            if (ActionObservations.Held(chef) != null) yield return Do("place_at", sink);
            if (ActionObservations.DirtyCount(sink) > 0)
            {
                TaskStep work = Do("work_at", sink, "sink_empty");
                work.FreeHands = cargo => TaskNative.Dirty(cargo) ? new[] { Do("place_at", sink) } : Park(cargo);
                yield return work;
            }
        }
    }
}
