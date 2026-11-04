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
            RefreshWorld(); GameObject chosen = null; float distance = Single.MaxValue;
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
            GameObject held = ActionObservations.Held(chef); if (held != null && !TaskNative.Dirty(held)) return false;
            GameObject sink = Sink();
            return sink != null && (TaskNative.Dirty(held) || DirtySources().Count > 0 || ActionObservations.DirtyCount(sink) > 0);
        }
        private IEnumerable<TaskStep> CollectDirtyBatch()
        {
            // Freeze currently obtainable stacks. Native placement combines the
            // entire carried stack into the next, then pickup takes the aggregate.
            // New arrivals belong to a later repetition, not an expanding batch.
            List<LogisticsItem> batch = DirtySources();
            foreach (LogisticsItem source in batch)
            {
                if (System.Object.ReferenceEquals(ActionObservations.Held(chef), source.Item)) continue;
                yield return Until("waiting_for_dirty_stack", delegate {
                    return source.Item != null && source.Source != null && source.Source.activeInHierarchy &&
                        (source.Item == source.Source || System.Object.ReferenceEquals(ActionObservations.Occupant(source.Source), source.Item));
                });
                if (ActionObservations.Held(chef) != null) yield return Do("place_at", source.Source);
                yield return Do("pick_up", source.Source);
            }
        }
        private IEnumerable<TaskStep> WashDishes()
        {
            GameObject sink = null;
            yield return Until("waiting_for_sink_access", delegate { sink = Sink(); return sink != null; });
            foreach (TaskStep step in CollectDirtyBatch()) yield return step;
            if (ActionObservations.Held(chef) != null) yield return Do("place_at", sink);
            if (ActionObservations.DirtyCount(sink) > 0) yield return Do("work_at", sink, "sink_empty");
        }
    }
}
