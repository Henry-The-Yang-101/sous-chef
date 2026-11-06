using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterBurnt() { Register("toss_burnt_food", delegate { return BurntSource() != null; }, TossBurntFood); }
        private LogisticsItem BurntSource()
        {
            inventory.Refresh(chef); LogisticsItem best = null; float distance = Single.MaxValue;
            foreach (LogisticsItem item in inventory.Items)
            {
                if (item.Supply || item.InUse || !TaskNative.Burnt(item.Item) || (item.Empty && item.Item.GetComponent<ServerIngredientContainer>() != null) ||
                    LogisticsChecks.Collect(chef, item, "move_item", true) != null) continue;
                if (item.Distance < distance) { distance = item.Distance; best = item; }
            }
            return best;
        }
        private GameObject RubbishBin()
        {
            inventory.Refresh(chef); GameObject best = null; float distance = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
                if (target.GetComponent<ServerRubbishBin>() != null && inventory.DistanceTo(target) < distance)
                { best = target; distance = inventory.DistanceTo(target); }
            return best;
        }
        private IEnumerable<TaskStep> TossBurntFood()
        {
            while (MoreRequested)
            {
                LogisticsItem source = null;
                yield return Until("waiting_for_burnt_food", delegate { source = BurntSource(); return source != null; });
                GameObject item = source.Item;
                GameObject previous = source.Holder ?? inventory.Home(item);
                GameObject burner = previous != null && previous.GetComponent<CookingStation>() != null ? previous : null;
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) yield return Do("pick_up", source.Source);
                GameObject bin = null;
                yield return Until("waiting_for_rubbish_bin", delegate { bin = RubbishBin(); return bin != null; });
                yield return Do("discard", bin);
                GameObject remaining = ActionObservations.Held(chef);
                if (remaining != null)
                {
                    // Native disposal empties pots instead of deleting them. Return
                    // this exact utensil, retaining its pre-disposal burner identity.
                    foreach (TaskStep step in Park(remaining, burner, true)) yield return step;
                }
                completed++;
            }
        }
    }
}
