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
            if (held != null && !TaskNative.Dirty(held)) return false;
            return Sink(true) != null && (TaskNative.Dirty(held) || DirtySources().Count > 0);
        }
        private IEnumerable<TaskStep> DeliverDirtyDishes()
        {
            foreach (TaskStep step in CollectDirtyBatch()) yield return step;
            GameObject dishes = ActionObservations.Held(chef);
            if (dishes == null) yield break;
            foreach (TaskStep step in Give(dishes)) yield return step;
        }
    }
}
