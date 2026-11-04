using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class PreparationUnit
        {
            public PreparationRequest Request;
            public GameObject Item, Station;
            public bool Processing, Delivered;
        }
        partial void RegisterPreparation()
        { Register("prepare_near_player", PreparationAvailable, PrepareNearPlayer); }
        private bool PreparationAvailable()
        {
            if (ActionObservations.Held(chef) != null || Human() == null) return false;
            foreach (LogisticsItem item in inventory.Items)
                if ((item.IngredientId != null || item.CleanPlate || item.UtensilType != null) &&
                    LogisticsChecks.Collect(chef, item, item.CleanPlate ? "get_plate" : "move_item") == null) return true;
            return false;
        }
        private GameObject Board(GameObject item)
        {
            inventory.Refresh(chef); GameObject best = null; float score = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
                if (target.GetComponent<ServerWorkstation>() != null && Reachable(target) && CanDeliver(item, target) && inventory.DistanceTo(target) < score)
                { best = target; score = inventory.DistanceTo(target); }
            return best;
        }
        private GameObject Processor(GameObject item, bool mixing)
        {
            inventory.Refresh(chef); GameObject best = null; float cost = Single.MaxValue;
            foreach (LogisticsItem candidate in inventory.Items)
            {
                if (!candidate.Empty || candidate.InUse || !TaskNative.Equipment(candidate.Source) || !Reachable(candidate.Source) ||
                    TaskNative.Component(candidate.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", item, candidate.Source, out operation) != null) continue;
                float distance = inventory.DistanceTo(candidate.Source);
                if (distance < cost) { cost = distance; best = candidate.Source; }
            }
            return best;
        }
        private IEnumerable<TaskStep> PrepareNearPlayer()
        {
            var units = new List<PreparationUnit>();
            foreach (PreparationRequest item in request.Items)
                for (int n = 0; n < item.Quantity; n++) units.Add(new PreparationUnit { Request = item });
            while (true)
            {
                PreparationUnit next = null; float soonest = Single.MaxValue;
                // Ready equipment has priority, ordered by remaining cooking
                // margin. Otherwise start passive processing before short jobs.
                foreach (PreparationUnit unit in units)
                {
                    if (unit.Delivered) continue;
                    if (unit.Processing)
                    {
                        if (!TaskNative.Ready(unit.Item)) continue;
                        float margin = TaskNative.Remaining(unit.Item, unit.Request.Preparation == "mixed");
                        if (next == null || !next.Processing || margin < soonest) { next = unit; soonest = margin; }
                    }
                    else if (next == null || (!next.Processing && Passive(unit.Request) && !Passive(next.Request))) next = unit;
                }
                if (next == null)
                {
                    bool done = true; foreach (PreparationUnit unit in units) if (!unit.Delivered) done = false;
                    if (done) yield break;
                    yield return Until("waiting_for_prepared_ingredients", delegate {
                        foreach (PreparationUnit unit in units) if (!unit.Delivered && unit.Processing && TaskNative.Ready(unit.Item)) return true;
                        return false;
                    });
                    continue;
                }
                if (next.Processing)
                {
                    // Preserve the exact claimed container while another item
                    // was prepared; never silently collect a replacement meal.
                    PreparationUnit selected = next;
                    yield return Until("waiting_for_original_preparation", delegate { return selected.Item != null && System.Object.ReferenceEquals(ActionObservations.Occupant(selected.Station), selected.Item); });
                    yield return Do("pick_up", next.Station);
                    foreach (TaskStep give in Give(next.Item, request.Player)) yield return give;
                    next.Delivered = true; continue;
                }
                LogisticsItem source = null; bool utensil = next.Request.Preparation == "empty";
                PreparationUnit current = next;
                yield return Until("waiting_for_ingredient", delegate { source = Source(current.Request.Selector, utensil); return source != null; });
                yield return Do("pick_up", source.Source);
                next.Item = ActionObservations.Held(chef);
                if (!utensil && next.Request.Preparation != "raw" && next.Item.GetComponent<WorkableItem>() != null)
                {
                    GameObject board = null;
                    yield return Until("waiting_for_chopping_board", delegate { board = Board(current.Item); return board != null; });
                    yield return Do("place_at", board);
                    yield return Do("work_at", board, "work_complete");
                    yield return Do("pick_up", board);
                    next.Item = ActionObservations.Held(chef);
                }
                if (Passive(next.Request))
                {
                    GameObject processor = null;
                    yield return Until("waiting_for_compatible_equipment", delegate { processor = Processor(current.Item, current.Request.Preparation == "mixed"); return processor != null; });
                    yield return Do("add_ingredient", processor);
                    next.Station = processor; next.Item = ActionObservations.Receiver(processor); next.Processing = true;
                    // Native mixers/ovens may require an explicit activation.
                    Component mixer = TaskNative.Component(processor, "ServerMixingStation");
                    if (mixer != null && !ActionObservations.Read<bool>(mixer, "m_bMixerOn")) yield return Do("activate", processor);
                    // No wait child occupies P2 while native time advances.
                    continue;
                }
                foreach (TaskStep give in Give(next.Item, request.Player)) yield return give;
                next.Delivered = true;
            }
        }
        private static bool Passive(PreparationRequest request) { return request.Preparation == "cooked" || request.Preparation == "mixed"; }
    }
}
