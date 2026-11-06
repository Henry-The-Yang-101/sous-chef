using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterDirtyDelivery() { Register("deliver_dirty_dishes", DirtyDeliveryAvailable, DeliverDirtyDishes); }
        private bool DirtyDeliveryAvailable()
        {
            GameObject held = ActionObservations.Held(chef);
            return (TaskNative.Dirty(held) || DirtySources().Count > 0) && Sink(true) != null;
        }
        private IEnumerable<TaskStep> DeliverDirtyDishes()
        {
            while (MoreRequested)
            {
                yield return Until("waiting_for_dirty_dishes", DirtyDeliveryAvailable);
                foreach (TaskStep step in DeliverDirtyBatch()) yield return step;
                completed++;
            }
        }
        private IEnumerable<TaskStep> DeliverDirtyBatch()
        {
            foreach (TaskStep step in CollectDirtyBatch()) yield return step;
            GameObject dishes = ActionObservations.Held(chef);
            if (dishes == null) yield break;
            foreach (TaskStep step in Give(dishes)) yield return step;
        }
    }
}
