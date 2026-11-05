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
        private bool PreparedIngredientMatches(LogisticsItem candidate, string selector)
        {
            // A preparation request names a dispenser root. Follow its native
            // work outputs to recognize supplied food at later stages without
            // weakening exact raw identities in the logistics API.
            foreach (LogisticsItem root in inventory.Items)
            {
                if (!root.Supply || !LogisticsInventory.IngredientMatches(root, selector)) continue;
                var seen = new HashSet<GameObject>(); GameObject stage = root.Item;
                while (stage != null && seen.Add(stage))
                {
                    var workable = stage.GetComponent<WorkableItem>();
                    stage = workable == null ? null : workable.GetNextPrefab();
                    string id, name;
                    if (LogisticsInventory.IngredientIdentity(stage, out id, out name) && id == candidate.IngredientId) return true;
                }
            }
            return false;
        }
        private static int PreparationWork(GameObject item)
        {
            int count = 0; var seen = new HashSet<GameObject>();
            while (item != null && seen.Add(item))
            {
                var workable = item.GetComponent<WorkableItem>();
                if (workable == null) return count;
                count++; item = workable.GetNextPrefab();
            }
            return count;
        }
        private LogisticsItem PreparationSource(PreparationRequest desired)
        { return Source(desired.Selector, desired.Preparation == "empty", desired.Preparation != "raw" && desired.Preparation != "empty"); }
        private bool PreparationAvailable()
        {
            if (Human() == null) return false;
            foreach (LogisticsItem item in inventory.Items)
                if ((item.IngredientId != null || item.CleanPlate || item.UtensilType != null) &&
                    Obtainable(item, item.CleanPlate ? "get_plate" : "move_item")) return true;
            return false;
        }
        private bool HeldPreparation(GameObject held, PreparationRequest desired)
        {
            if (held == null || deliveredItems.Contains(held)) return false;
            inventory.Refresh(chef);
            foreach (LogisticsItem entry in inventory.Items)
            {
                if (!System.Object.ReferenceEquals(entry.Item, held)) continue;
                if (desired.Preparation == "empty")
                {
                    string type = LogisticsInventory.CanonicalUtensil(desired.Selector);
                    return type == "plate" ? entry.CleanPlate : entry.Empty && entry.UtensilType == type;
                }
                return LogisticsInventory.IngredientMatches(entry, desired.Selector) ||
                    desired.Preparation != "raw" && PreparedIngredientMatches(entry, desired.Selector);
            }
            return false;
        }
        private bool UsefulPreparation(GameObject held, List<PreparationUnit> units)
        {
            foreach (PreparationUnit unit in units)
                if (!unit.Delivered && !unit.Processing && HeldPreparation(held, unit.Request)) return true;
            return false;
        }
        private static bool NeedsWork(PreparationRequest request, GameObject item)
        { return request.Preparation != "raw" && request.Preparation != "empty" && item != null && item.GetComponent<WorkableItem>() != null; }
        private static bool OnBoard(LogisticsItem entry)
        { return entry.Holder != null && entry.Holder.GetComponent<ServerWorkstation>() != null; }
        // A free compatible board, or null. `occupied` reports a reachable board
        // that only its current contents keep from receiving this food.
        private GameObject Board(GameObject item, out bool occupied, bool supplyPrefab = false)
        {
            inventory.Refresh(chef); GameObject best = null; float score = Single.MaxValue; occupied = false;
            foreach (GameObject target in navigation.Targets())
            {
                if (target.GetComponent<ServerWorkstation>() == null) continue;
                float cost = ApproachCost(target);
                if (cost == Single.MaxValue) continue;
                if (!CanDeliver(item, target, supplyPrefab))
                {
                    GameObject occupant = ActionObservations.Occupant(target);
                    if (occupant != null && !System.Object.ReferenceEquals(occupant, item)) occupied = true;
                    continue;
                }
                if (cost < score) { best = target; score = cost; }
            }
            return best;
        }
        // The nearest reachable board whose contents P2 may move aside.
        private GameObject MovableBoardContents()
        {
            GameObject best = null; float cost = Single.MaxValue;
            foreach (LogisticsItem entry in inventory.Items)
            {
                if (!OnBoard(entry) || entry.InUse || !Obtainable(entry, "move_item")) continue;
                float approach = ApproachCost(entry.Holder);
                if (approach < cost) { best = entry.Holder; cost = approach; }
            }
            return best;
        }
        // Food already on a board is worked in place. Otherwise a unit needing
        // work may only collect food when a board can receive it: holding cargo
        // while every reachable board is occupied would keep P2 from clearing
        // the very boards it is waiting for. Unknown routes keep the existing
        // behaviour; the active child still proves them before interacting.
        private LogisticsItem StartingSource(PreparationUnit unit, ref bool boardBlocked)
        {
            LogisticsItem source = PreparationSource(unit.Request);
            if (source == null || !NeedsWork(unit.Request, source.Item) || OnBoard(source)) return source;
            bool occupied;
            if (Board(source.Item, out occupied, source.Supply) != null || !occupied) return source;
            boardBlocked = true; return null;
        }
        private GameObject Processor(GameObject item, bool mixing)
        {
            inventory.Refresh(chef); GameObject best = null; float cost = Single.MaxValue;
            foreach (LogisticsItem candidate in inventory.Items)
            {
                if (!candidate.Empty || candidate.InUse || !TaskNative.Equipment(candidate.Holder ?? candidate.Source) ||
                    TaskNative.Component(candidate.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", item, candidate.Source, out operation) != null) continue;
                float distance = ApproachCost(candidate.Source);
                if (distance < cost) { cost = distance; best = candidate.Source; }
            }
            return best;
        }
        private bool EquipmentPair(GameObject food, bool mixing, out LogisticsItem container, out GameObject station)
        {
            inventory.Refresh(chef); container = null; station = null;
            foreach (LogisticsItem item in inventory.Items)
            {
                if (!item.Empty || item.InUse || ApproachCost(item.Source) == Single.MaxValue || !LogisticsChecks.CanCollectEmpty(chef, item.Source) ||
                    TaskNative.Component(item.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", food, item.Item, out operation) != null) continue;
                foreach (GameObject target in navigation.Targets())
                    if (TaskNative.Equipment(target) && ApproachCost(target) != Single.MaxValue && ActionObservations.Occupant(target) == null && CanDeliver(item.Item, target))
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
                PreparationUnit next = null; float soonest = Single.MaxValue; bool boardBlocked = false, nextHeld = false;
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
                        if (next == null || !next.Processing || margin < soonest) { next = unit; soonest = margin; nextHeld = false; }
                    }
                    else if (!Passive(unit.Request) || (unit.Request.Preparation == "mixed" ? emptyMixer : emptyCooker))
                    {
                        LogisticsItem candidate = StartingSource(unit, ref boardBlocked);
                        if (candidate == null) continue;
                        bool held = System.Object.ReferenceEquals(candidate.Item, ActionObservations.Held(chef));
                        if (next == null || !next.Processing && (held && !nextHeld || held == nextHeld && Passive(unit.Request) && !Passive(next.Request)))
                        { next = unit; nextHeld = held; }
                    }
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
                            if (unit.Processing || unit.Delivered || (Passive(unit.Request) && !(unit.Request.Preparation == "mixed" ? emptyMixer : emptyCooker)) ||
                                StartingSource(unit, ref boardBlocked) == null) continue;
                            float estimate; if (!estimates.TryGetValue(unit.Request, out estimate)) { estimate = PreparationSeconds(unit); estimates.Add(unit.Request, estimate); }
                            if (estimate + 2 < availableTime && (fits == null ||
                                HeldPreparation(ActionObservations.Held(chef), unit.Request) && !HeldPreparation(ActionObservations.Held(chef), fits.Request) ||
                                Passive(unit.Request) && !Passive(fits.Request) && !HeldPreparation(ActionObservations.Held(chef), fits.Request))) fits = unit;
                        }
                        next = fits;
                    }
                }
                if (next == null)
                {
                    bool done = true; foreach (PreparationUnit unit in units) if (!unit.Delivered) done = false;
                    if (done) yield break;
                    // Every reachable board holds food the remaining units cannot
                    // use now, such as a later repetition's. Only P2 can free one.
                    GameObject full = boardBlocked ? MovableBoardContents() : null;
                    if (full != null)
                    {
                        TaskStep lift = Do("pick_up", full); lift.ReplanOnBlock = true;
                        yield return lift;
                        if (lift.Succeeded) foreach (TaskStep park in Park(ActionObservations.Held(chef))) yield return park;
                        continue;
                    }
                    // Revisit the scheduler as route discovery progresses. A
                    // currently unconfirmed ingredient route must not occupy the
                    // entire cooking window when it becomes available next frame.
                    float retry = Time.realtimeSinceStartup + .2f;
                    yield return Until(boardBlocked ? "waiting_for_chopping_board" : processing ? "waiting_for_prepared_ingredients" : "waiting_for_ingredient",
                        delegate { return Time.realtimeSinceStartup >= retry; });
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
                        yield return GetServingUtensil(serving, next.Item);
                        yield return Do("collect_contents", next.Station);
                    }
                    else yield return Do("pick_up", next.Station);
                    GameObject meal = ActionObservations.Held(chef);
                    foreach (TaskStep give in Give(meal, request.Player, true, true)) yield return give;
                    // A lost pass prepares this unit again from what remains.
                    if (!deliveredItems.Contains(meal)) { next.Processing = false; next.Item = next.Station = null; continue; }
                    next.Delivered = true; continue;
                }
                LogisticsItem source = PreparationSource(next.Request);
                PreparationUnit current = next;
                if (source == null) continue;
                GameObject board = NeedsWork(next.Request, source.Item) && OnBoard(source) ? source.Holder : null;
                if (board == null)
                {
                    TaskStep pickup = Do("pick_up", ActionObservations.PickupTarget(source.Source, source.Supply ? null : source.Item));
                    pickup.UseHeld = held => HeldPreparation(held, current.Request);
                    pickup.ReplanForHeld = held => UsefulPreparation(held, units);
                    pickup.ReplanOnBlock = true; yield return pickup;
                    if (!pickup.Succeeded) { foreach (TaskStep clear in Clear(pickup.Obstruction)) yield return clear; continue; }
                }
                next.Item = board == null ? ActionObservations.Held(chef) : source.Item;
                bool rescheduled = false; GameObject obstruction = null;
                while (NeedsWork(next.Request, next.Item))
                {
                    if (board == null)
                    {
                        bool occupied = false;
                        yield return Until("waiting_for_chopping_board", delegate { board = Board(current.Item, out occupied); return board != null || occupied; });
                        if (board == null)
                        {
                            // Other food reached every board after our pickup. Set
                            // ours down so the scheduler can work what holds them.
                            foreach (TaskStep park in Park(current.Item)) yield return park;
                            rescheduled = true; break;
                        }
                        TaskStep placement = Do("place_at", board); placement.ReplanOnBlock = true;
                        yield return placement;
                        if (!placement.Succeeded)
                        {
                            board = null; obstruction = placement.Obstruction;
                            if (obstruction == null) continue;
                            rescheduled = true; break;
                        }
                    }
                    // Before a press, blocked work leaves the food on its board;
                    // the scheduler resumes it there as an in-place source.
                    TaskStep work = Do("work_at", board, "work_complete"); work.ReplanOnBlock = true;
                    work.ReplanForHeld = held => UsefulPreparation(held, units);
                    yield return work;
                    if (!work.Succeeded) { obstruction = work.Obstruction; rescheduled = true; break; }
                    yield return Do("pick_up", board);
                    next.Item = ActionObservations.Held(chef); board = null;
                }
                if (rescheduled)
                {
                    foreach (TaskStep clear in Clear(obstruction)) yield return clear;
                    next.Item = null; continue;
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
                GameObject prepared = next.Item;
                foreach (TaskStep give in Give(prepared, request.Player, true, true)) yield return give;
                if (!deliveredItems.Contains(prepared)) { next.Item = null; continue; }
                next.Delivered = true;
            }
        }
        private static bool Passive(PreparationRequest request) { return request.Preparation == "cooked" || request.Preparation == "mixed"; }
        private bool EmptyProcessor(bool mixing)
        {
            inventory.Refresh(chef);
            foreach (LogisticsItem item in inventory.Items)
                if (item.Empty && !item.InUse && TaskNative.Equipment(item.Holder ?? item.Source) && ApproachCost(item.Source) != Single.MaxValue &&
                    TaskNative.Component(item.Item, mixing ? "ServerMixingHandler" : "ServerCookingHandler") != null) return true;
            return false;
        }
        private float PreparationSeconds(PreparationUnit unit)
        {
            LogisticsItem source = PreparationSource(unit.Request);
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
