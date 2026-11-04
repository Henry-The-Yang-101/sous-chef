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
                if (!candidate.Empty || candidate.InUse || !TaskNative.Equipment(candidate.Holder ?? candidate.Source) || !Reachable(candidate.Source) ||
                    TaskNative.Component(candidate.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", item, candidate.Source, out operation) != null) continue;
                float distance = inventory.DistanceTo(candidate.Source);
                if (distance < cost) { cost = distance; best = candidate.Source; }
            }
            return best;
        }
        private bool EquipmentPair(GameObject food, bool mixing, out LogisticsItem container, out GameObject station)
        {
            inventory.Refresh(chef); container = null; station = null;
            foreach (LogisticsItem item in inventory.Items)
            {
                if (!item.Empty || item.InUse || item.Distance == Single.MaxValue || !LogisticsChecks.CanCollectEmpty(chef, item.Source) ||
                    TaskNative.Component(item.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", food, item.Item, out operation) != null) continue;
                foreach (GameObject target in navigation.Targets())
                    if (TaskNative.Equipment(target) && Reachable(target) && ActionObservations.Occupant(target) == null && CanDeliver(item.Item, target))
                    { container = item; station = target; return true; }
            }
            return false;
        }
        private IEnumerable<TaskStep> PrepareNearPlayer()
        {
            var units = new List<PreparationUnit>();
            foreach (PreparationRequest item in request.Items)
                for (int n = 0; n < item.Quantity; n++) units.Add(new PreparationUnit { Request = item });
            while (true)
            {
                PreparationUnit next = null; float soonest = Single.MaxValue;
                bool processing = false; foreach (PreparationUnit unit in units) if (unit.Processing && !unit.Delivered) processing = true;
                bool emptyCooker = !processing || EmptyProcessor(false), emptyMixer = !processing || EmptyProcessor(true);
                // Ready equipment has priority, ordered by remaining cooking
                // margin. Otherwise start passive processing before short jobs.
                foreach (PreparationUnit unit in units)
                {
                    if (unit.Delivered) continue;
                    if (unit.Processing)
                    {
                        if (!TaskNative.Ready(unit.Item, unit.Request.Preparation == "mixed")) continue;
                        float margin = TaskNative.Remaining(unit.Item, unit.Request.Preparation == "mixed");
                        if (next == null || !next.Processing || margin < soonest) { next = unit; soonest = margin; }
                    }
                    else if ((!Passive(unit.Request) || (unit.Request.Preparation == "mixed" ? emptyMixer : emptyCooker)) &&
                        (next == null || (!next.Processing && Passive(unit.Request) && !Passive(next.Request)))) next = unit;
                }
                // Avoid beginning an indivisible chopping/travel segment that
                // would carry us past another pot's native completion time.
                // Short jobs can still fill the available cooking interval.
                if (next != null && !next.Processing)
                {
                    float availableTime = Single.MaxValue;
                    foreach (PreparationUnit unit in units)
                        if (unit.Processing && !unit.Delivered) availableTime = Math.Min(availableTime, TaskNative.Remaining(unit.Item, unit.Request.Preparation == "mixed"));
                    if (availableTime != Single.MaxValue)
                    {
                        PreparationUnit fits = null;
                        var estimates = new Dictionary<PreparationRequest, float>();
                        foreach (PreparationUnit unit in units)
                        {
                            if (unit.Processing || unit.Delivered || (Passive(unit.Request) && !(unit.Request.Preparation == "mixed" ? emptyMixer : emptyCooker))) continue;
                            float estimate; if (!estimates.TryGetValue(unit.Request, out estimate)) { estimate = PreparationSeconds(unit); estimates.Add(unit.Request, estimate); }
                            if (estimate + 2 < availableTime && (fits == null || Passive(unit.Request) && !Passive(fits.Request))) fits = unit;
                        }
                        next = fits;
                    }
                }
                if (next == null)
                {
                    bool done = true; foreach (PreparationUnit unit in units) if (!unit.Delivered) done = false;
                    if (done) yield break;
                    // Revisit the scheduler as route discovery progresses. A
                    // currently unconfirmed ingredient route must not occupy the
                    // entire cooking window when it becomes available next frame.
                    float retry = Time.realtimeSinceStartup + .2f;
                    yield return Until("waiting_for_prepared_ingredients", delegate { return Time.realtimeSinceStartup >= retry; });
                    continue;
                }
                if (next.Processing)
                {
                    // Preserve the exact claimed container while another item
                    // was prepared; never silently collect a replacement meal.
                    PreparationUnit selected = next;
                    yield return Until("waiting_for_original_preparation", delegate { return selected.Item != null && System.Object.ReferenceEquals(ActionObservations.Receiver(selected.Station), selected.Item); });
                    LogisticsItem serving = ServingUtensil(next.Item, next.Station);
                    if (serving != null)
                    {
                        yield return Do("pick_up", serving.Source);
                        yield return Do("collect_contents", next.Station);
                    }
                    else yield return Do("pick_up", next.Station);
                    foreach (TaskStep give in Give(ActionObservations.Held(chef), request.Player, true)) yield return give;
                    next.Delivered = true; continue;
                }
                LogisticsItem source = null; bool utensil = next.Request.Preparation == "empty";
                PreparationUnit current = next;
                yield return Until("waiting_for_ingredient", delegate { source = Source(current.Request.Selector, utensil); return source != null; });
                TaskStep pickup = Do("pick_up", ActionObservations.PickupTarget(source.Source, source.Supply ? null : source.Item));
                pickup.ReplanOnBlock = true; yield return pickup;
                if (!pickup.Succeeded) continue;
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
                    LogisticsItem empty = null; GameObject equipment = null;
                    yield return Until("waiting_for_compatible_equipment", delegate {
                        processor = Processor(current.Item, current.Request.Preparation == "mixed");
                        return processor != null || EquipmentPair(current.Item, current.Request.Preparation == "mixed", out empty, out equipment);
                    });
                    if (processor == null)
                    {
                        GameObject staging = null;
                        yield return Until("waiting_for_ingredient_staging", delegate { staging = Parking(current.Item); return staging != null; });
                        yield return Do("place_at", staging);
                        yield return Do("pick_up", empty.Source);
                        yield return Do("place_at", equipment);
                        yield return Do("pick_up", staging); processor = equipment;
                    }
                    yield return Do("add_ingredient", processor);
                    next.Station = processor; next.Item = ActionObservations.Receiver(processor); next.Processing = true;
                    // Native mixers/ovens may require an explicit activation.
                    Component mixer = TaskNative.Component(processor, "ServerMixingStation");
                    if (mixer != null && !ActionObservations.Read<bool>(mixer, "m_bMixerOn")) yield return Do("activate", processor);
                    Component cooker = TaskNative.Component(processor, "ServerCookingStation") ?? TaskNative.Component(processor, "ServerHeatedCookingStation");
                    if (cooker != null && !ActionObservations.Read<bool>(cooker, "m_isTurnedOn") && processor.GetComponent<ClientInteractable>() != null) yield return Do("activate", processor);
                    // No wait child occupies P2 while native time advances.
                    continue;
                }
                foreach (TaskStep give in Give(next.Item, request.Player, true)) yield return give;
                next.Delivered = true;
            }
        }
        private static bool Passive(PreparationRequest request) { return request.Preparation == "cooked" || request.Preparation == "mixed"; }
        private bool EmptyProcessor(bool mixing)
        {
            inventory.Refresh(chef);
            foreach (LogisticsItem item in inventory.Items)
                if (item.Empty && !item.InUse && TaskNative.Equipment(item.Holder ?? item.Source) && item.Distance != Single.MaxValue &&
                    TaskNative.Component(item.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") != null) return true;
            return false;
        }
        private float PreparationSeconds(PreparationUnit unit)
        {
            LogisticsItem source = Source(unit.Request.Selector, unit.Request.Preparation == "empty");
            if (source == null) return Single.MaxValue;
            // Travel uses certified route distance and the native chef speed.
            // Chopping is an estimate (animation/collaboration can change it),
            // while equipment readiness uses exact native progress and duration.
            float travel = source.Distance / Math.Max(.1f, chef.Movement.RunSpeed);
            var workable = source.Item.GetComponent<WorkableItem>();
            float chopping = unit.Request.Preparation != "raw" && unit.Request.Preparation != "empty" && workable != null
                ? Math.Max(1, ActionObservations.Read<int>(workable, "m_stages")) * .3f : 0;
            return travel * 2 + chopping + 2;
        }
    }
}
