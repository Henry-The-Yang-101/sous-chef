using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterPreparation()
        { RegisterForRequest("prepare_near_player", PreparationAvailable, PrepareNearPlayer); }
        // Resolve aliases against the native spawn trees once per selector
        // observation, not once per candidate (raw and chopped may share a name).
        private HashSet<string> PreparationIdentities(string selector, bool processed)
        {
            var ids = new HashSet<string>();
            foreach (PreparationIngredient root in inventory.IngredientRoots)
            {
                if (!LogisticsInventory.IngredientMatches(root.Id, root.Name, selector) || !ids.Add(root.Id) || !processed) continue;
                var seen = new HashSet<GameObject>();
                for (GameObject stage = root.Prefab; stage != null && seen.Add(stage); )
                {
                    var workable = stage.GetComponent<WorkableItem>();
                    stage = workable == null ? null : workable.GetNextPrefab();
                    string id, name;
                    if (LogisticsInventory.IngredientIdentity(stage, out id, out name)) ids.Add(id);
                }
            }
            return ids;
        }
        private static int PreparationWork(GameObject item)
        {
            if (item == null || item.GetComponent<WorkableItem>() == null) return 0;
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
        {
            inventory.Refresh(chef);
            string selector = desired.Selector;
            bool utensil = desired.Preparation == "empty", processed = !utensil && desired.Preparation != "raw";
            string utensilType = utensil ? LogisticsInventory.CanonicalUtensil(selector) : null;
            bool plate = utensilType == "plate";
            LogisticsItem best = null; float score = Single.MaxValue; int bestWork = Int32.MaxValue;
            bool bestPlaced = false, bestHeld = false; float bestProgress = -1;
            var ids = utensil ? null : PreparationIdentities(selector, processed);
            GameObject cargo = ActionObservations.Held(chef);
            foreach (LogisticsItem entry in inventory.Items)
            {
                if (!entry.Supply && (deliveredItems.Contains(entry.Item) || preparation != null && preparation.Reserved(entry.Item))) continue;
                bool match = utensil ? plate ? entry.CleanPlate : entry.Empty && entry.UtensilType == utensilType :
                    ids.Count == 0 ? LogisticsInventory.IngredientMatches(entry, selector) : ids.Contains(entry.IngredientId);
                if (!match || !Obtainable(entry, plate ? "get_plate" : "move_item")) continue;
                if (!utensil && TaskNative.Equipment(entry.Holder ?? entry.Source)) continue;
                // For equivalent preparation stages, prefer certified sources.
                float candidate = (entry.Distance != Single.MaxValue ? entry.Distance : ApproachCost(entry.Source)) + (entry.Supply ? 10000 : 0);
                int work = processed ? PreparationWork(entry.Item) : 0;
                float progress = processed && !entry.Supply ? PreparationProgress(entry.Item) : 0;
                bool held = System.Object.ReferenceEquals(cargo, entry.Item);
                // Food already on a board needs no placement and frees that board.
                bool placed = work > 0 && OnBoard(entry);
                if (work < bestWork || work == bestWork && (progress > bestProgress || progress == bestProgress &&
                    (placed && !bestPlaced || placed == bestPlaced && (held && !bestHeld || held == bestHeld && candidate < score))))
                { best = entry; score = candidate; bestWork = work; bestProgress = progress; bestPlaced = placed; bestHeld = held; }
            }
            return best;
        }
        private bool PreparationAvailable(TaskRequest desired)
        {
            // Source and route availability are handled by the running plan.
            // A conveyor spawn or a route may become usable after dispatch.
            if (desired != null) return Human(desired.Player) != null;
            return Human(1) != null || Human(3) != null || Human(4) != null;
        }
        private bool HeldPreparation(GameObject held, PreparationRequest desired)
        {
            if (held == null || deliveredItems.Contains(held) || preparation != null && preparation.Reserved(held)) return false;
            inventory.Refresh(chef);
            foreach (LogisticsItem entry in inventory.Items)
            {
                if (!System.Object.ReferenceEquals(entry.Item, held)) continue;
                if (desired.Preparation == "empty")
                {
                    string type = LogisticsInventory.CanonicalUtensil(desired.Selector);
                    return type == "plate" ? entry.CleanPlate : entry.Empty && entry.UtensilType == type;
                }
                var ids = PreparationIdentities(desired.Selector, desired.Preparation != "raw");
                return ids.Count == 0 ? LogisticsInventory.IngredientMatches(entry, desired.Selector) : ids.Contains(entry.IngredientId);
            }
            return false;
        }
        private static float PreparationProgress(GameObject item)
        {
            var work = TaskNative.Component<ServerWorkableItem>(item);
            return work == null ? 0 : work.GetProgress() + ActionObservations.Read<int>(work, "m_subProgress") * .001f;
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
                float cost = inventory.DistanceTo(target);
                if (cost == Single.MaxValue)
                {
                    if ((bool)inventory.Catalog["complete"] || !navigation.SharesCurrentSupport(target) ||
                        !navigation.CanApproachLocally(target)) continue;
                    cost = 20000 + Vector3.Distance(chef.transform.position, target.transform.position);
                }
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
        // the very boards it is waiting for. An unknown route is not a free board.
        private enum BoardWait { None, Unreachable, Occupied }
        private bool PreparationBoardAvailable(PreparationRequest desired, LogisticsItem source, ref BoardWait boardWait)
        {
            if (!NeedsWork(desired, source.Item) || OnBoard(source)) return true;
            bool occupied;
            if (Board(source.Item, out occupied, source.Supply) != null) return true;
            if (occupied) boardWait = BoardWait.Occupied;
            else if (boardWait == BoardWait.None) boardWait = BoardWait.Unreachable;
            return false;
        }
        private GameObject Processor(GameObject item, bool mixing)
        {
            inventory.Refresh(chef); GameObject best = null; float cost = Single.MaxValue;
            foreach (LogisticsItem candidate in inventory.Items)
            {
                if (!candidate.Empty || candidate.InUse || deliveredItems.Contains(candidate.Item) || !TaskNative.Equipment(candidate.Holder ?? candidate.Source) ||
                    TaskNative.ProcessingHandler(candidate.Item, mixing) == null) continue;
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
                if (!item.Empty || item.InUse || deliveredItems.Contains(item.Item) || ApproachCost(item.Source) == Single.MaxValue || !LogisticsChecks.CanCollectEmpty(chef, item.Source) ||
                    TaskNative.ProcessingHandler(item.Item, mixing) == null) continue;
                string operation;
                if (LogisticsChecks.Delivery(chef, "fetch_ingredient", food, item.Item, out operation) != null) continue;
                foreach (GameObject target in navigation.Targets())
                    if (TaskNative.Equipment(target) && ApproachCost(target) != Single.MaxValue && ActionObservations.Occupant(target) == null && CanDeliver(item.Item, target))
                    { container = item; station = target; return true; }
            }
            return false;
        }
        private static bool Passive(PreparationRequest order) { return order.Preparation == "cooked" || order.Preparation == "mixed"; }

        private float PreparationSeconds(PreparationRequest order, LogisticsItem source)
        {
            float distance = source.Distance == Single.MaxValue
                ? Vector3.Distance(chef.transform.position, source.Source.transform.position) : source.Distance;
            float travel = distance / Math.Max(.1f, chef.Movement.RunSpeed);
            var workable = source.Item.GetComponent<WorkableItem>();
            float chopping = NeedsWork(order, source.Item) ? Math.Max(1, ActionObservations.Read<int>(workable, "m_stages")) * .3f : 0;
            return travel * 2 + chopping + 4;
        }
    }
}
