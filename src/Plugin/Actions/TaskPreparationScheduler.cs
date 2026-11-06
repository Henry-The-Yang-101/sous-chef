using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private PreparationSchedule preparation;
        private sealed class PrepJob
        {
            public PreparationSchedule.Lane Lane;
            public LogisticsItem Source;
            public bool Parked;
        }

        private IEnumerable<TaskStep> PrepareNearPlayer()
        {
            preparation = new PreparationSchedule(request);
            while (MoreRequested)
            {
                inventory.Refresh(chef);
                ObserveParkedHandoffs();
                if (!MoreRequested) yield break;
                float availableSeconds;
                var ready = ObserveCooking(out availableSeconds);
                if (ready != null)
                {
                    foreach (TaskStep step in CollectBatch(ready)) yield return step;
                    continue;
                }
                BoardWait boardWait = BoardWait.None;
                PrepJob job = SelectPreparation(availableSeconds, ref boardWait);
                if (job != null)
                {
                    if (!job.Parked) preparation.Dispatch(job.Lane);
                    foreach (TaskStep step in job.Parked ? ResumeHandoff(job) : PrepareOne(job)) yield return step;
                    continue;
                }
                GameObject full = boardWait == BoardWait.Occupied && availableSeconds > 6 ? MovableBoardContents() : null;
                if (full != null)
                {
                    TaskStep lift = Reschedulable("pick_up", full);
                    yield return lift;
                    if (lift.Succeeded) foreach (TaskStep park in Park(ActionObservations.Held(chef))) yield return park;
                    continue;
                }
                // All candidates blocked. Back off instead of reading Unity on
                // every render frame; navigation continues its bounded search.
                float retry = Time.realtimeSinceStartup + .2f;
                yield return Until(boardWait != BoardWait.None ? "waiting_for_chopping_board" :
                    preparation.Cooking.Count != 0 ? "waiting_for_prepared_ingredients" : pendingDeliveries.Count != 0 ? "waiting_for_transport_delivery" : "waiting_for_ingredient",
                    () => Time.realtimeSinceStartup >= retry);
            }
        }

        private void ObserveParkedHandoffs()
        {
            foreach (var lane in preparation.Lanes)
            {
                GameObject item = lane.ParkedMeal;
                if (System.Object.ReferenceEquals(item, null)) continue;
                if (item == null || !item.activeInHierarchy) { lane.ParkedMeal = null; continue; }
                var human = Human(request.Player);
                if (human != null && System.Object.ReferenceEquals(ActionObservations.Held(human), item))
                { deliveredItems.Add(item); completed = preparation.Complete(lane, item); lane.ParkedMeal = null; }
                else if (Passive(lane.Order) && LogisticsInventory.Empty(item)) lane.ParkedMeal = null;
            }
        }

        private PreparationSchedule.Batch ObserveCooking(out float availableSeconds)
        {
            availableSeconds = Single.MaxValue;
            PreparationSchedule.Batch ready = null; float earliestBurn = Single.MaxValue;
            for (int i = preparation.Cooking.Count - 1; i >= 0; i--)
            {
                var batch = preparation.Cooking[i];
                // The human can empty, remove or replace a pan while P2 works.
                // Reopen its demand; never credit or interact with a substitute.
                if (!batch.Intact) { preparation.Release(batch); continue; }
                if (ApproachCost(batch.Station) == Single.MaxValue || Time.realtimeSinceStartup < batch.Lane.RetryAt) continue;
                if (TaskNative.Ready(batch.Container, batch.Mixing))
                {
                    float margin = TaskNative.Remaining(batch.Container, batch.Mixing, true);
                    if (margin < earliestBurn) { ready = batch; earliestBurn = margin; }
                }
                else
                {
                    // Completion is not a burn deadline. Use the remaining safe
                    // window so a nearby loose ingredient can fill another pot,
                    // reserving time to return and collect the oldest batch.
                    float rescue = ApproachCost(batch.Station) / Math.Max(.1f, chef.Movement.RunSpeed) + 2;
                    availableSeconds = Math.Min(availableSeconds,
                        Math.Max(0, TaskNative.Remaining(batch.Container, batch.Mixing, true) - rescue));
                }
            }
            return ready;
        }

        private PrepJob SelectPreparation(float availableSeconds, ref BoardWait boardWait, PreparationSchedule.Lane exclude = null)
        {
            GameObject held = ActionObservations.Held(chef); PrepJob best = null;
            var candidates = new List<PrepJob>(preparation.Lanes.Length);
            bool cookerBuffered = false, mixerBuffered = false;
            // Observe each lane's source once. A prepared successor waiting for
            // equipment caps speculative chopping across all lanes using it.
            for (int n = 0; n < preparation.Lanes.Length; n++)
            {
                var lane = preparation.At(n);
                if (lane == exclude || Time.realtimeSinceStartup < lane.RetryAt) continue;
                PrepJob job = null;
                if (lane.ParkedMeal != null)
                {
                    foreach (var entry in inventory.Items)
                        if (System.Object.ReferenceEquals(lane.ParkedMeal, entry.Item) && Obtainable(entry, "move_item"))
                        { job = new PrepJob { Lane = lane, Source = entry, Parked = true }; break; }
                }
                else if (lane.WantsMore)
                {
                    var source = PreparationSource(lane.Order);
                    if (source == null) continue;
                    job = new PrepJob { Lane = lane, Source = source };
                    if (!source.Supply && Passive(lane.Order) && !NeedsWork(lane.Order, source.Item))
                    {
                        if (lane.Order.Preparation == "mixed") mixerBuffered = true;
                        else cookerBuffered = true;
                    }
                }
                if (job != null) candidates.Add(job);
            }
            foreach (var job in candidates)
            {
                var order = job.Lane.Order;
                if (!job.Parked)
                {
                    if (availableSeconds != Single.MaxValue && availableSeconds <= PreparationSeconds(order, job.Source)) continue;
                    if (Passive(order) && !CanPrepareForProcessor(job.Lane, job.Source,
                        order.Preparation == "mixed" ? mixerBuffered : cookerBuffered)) continue;
                    if (!PreparationBoardAvailable(order, job.Source, ref boardWait)) continue;
                }
                if (PreferPreparation(job, best, held)) best = job;
            }
            return best;
        }

        private static bool ReadyToGive(PrepJob job)
        {
            return job.Parked || !Passive(job.Lane.Order) && !NeedsWork(job.Lane.Order, job.Source.Item);
        }
        private enum PreparationPriority { Fetch, Held, InProgress, LoadProcessor, Handoff }
        private PreparationPriority Priority(PrepJob job, GameObject held)
        {
            // Existing ready food always feeds the player first. A dispenser
            // can create unlimited raw handoffs, so keep it in round-robin turn
            // rather than starving another ingredient's unfinished preparation.
            bool supplyDue = job.Lane.Delivered + job.Lane.InTransit < (completed + 1) * job.Lane.Order.Quantity;
            if (ReadyToGive(job) && (!job.Source.Supply || supplyDue)) return PreparationPriority.Handoff;
            if (!job.Source.Supply && Passive(job.Lane.Order) && !NeedsWork(job.Lane.Order, job.Source.Item))
                return PreparationPriority.LoadProcessor;
            if (OnBoard(job.Source) || !job.Source.Supply && PreparationProgress(job.Source.Item) > 0) return PreparationPriority.InProgress;
            return System.Object.ReferenceEquals(held, job.Source.Item) ? PreparationPriority.Held : PreparationPriority.Fetch;
        }
        private bool PreferPreparation(PrepJob candidate, PrepJob current, GameObject held)
        {
            if (current == null) return true;
            var priority = Priority(candidate, held); var previous = Priority(current, held);
            if (priority != previous) return priority > previous;
            // Within the same class, use held cargo, then preserve the cursor's
            // round-robin order. Never let a raw catch outrank a ready handoff.
            return System.Object.ReferenceEquals(held, candidate.Source.Item) &&
                !System.Object.ReferenceEquals(held, current.Source.Item);
        }
        private bool ReadyPreparationWaiting()
        {
            BoardWait unused = BoardWait.None;
            PrepJob next = SelectPreparation(Single.MaxValue, ref unused);
            return next != null && ReadyToGive(next);
        }
        private bool CanPrepareForProcessor(PreparationSchedule.Lane lane, LogisticsItem source, bool buffered)
        {
            bool mixing = lane.Order.Preparation == "mixed";
            // Work-output and dispenser prefabs do not have the live network
            // transfer handlers. Check actual placement after spawning/chopping.
            if (NeedsWork(lane.Order, source.Item) || source.Supply)
            {
                foreach (var item in inventory.Items)
                    if (!item.InUse && item.Empty && TaskNative.ProcessingHandler(item.Item, mixing) != null &&
                        ApproachCost(item.Source) != Single.MaxValue) return true;
                // Full pan: prepare its successor on a board. Once chopped it
                // becomes the preferred source and cannot enter this branch.
                if (!buffered && NeedsWork(lane.Order, source.Item))
                    foreach (var batch in preparation.Cooking) if (batch.Mixing == mixing) return true;
                return false;
            }
            LogisticsItem empty; GameObject equipment;
            return Processor(source.Item, mixing) != null || EquipmentPair(source.Item, mixing, out empty, out equipment);
        }

        private bool UsefulCatch(GameObject held)
        {
            foreach (var lane in preparation.Lanes)
                if (lane.WantsMore && HeldPreparation(held, lane.Order)) return true;
            return false;
        }
        private TaskStep Reschedulable(string operation, GameObject target, string condition = null)
        {
            var step = Do(operation, target, condition);
            step.ReplanOnBlock = true;
            step.AbandonBeforePress = () => child.RouteUnavailable;
            return step;
        }

        private IEnumerable<TaskStep> PrepareOne(PrepJob job)
        {
            var lane = job.Lane; var order = lane.Order; var source = job.Source;
            GameObject board = NeedsWork(order, source.Item) && OnBoard(source) ? source.Holder : null;
            GameObject item = source.Item;
            if (board == null)
            {
                var pickup = Reschedulable("pick_up", ActionObservations.PickupTarget(source.Source, source.Supply ? null : source.Item));
                // A moving item can leave native pickup range after selection.
                // Once verification ends, select again from observed inventory;
                // UseHeld reconciles any acquired cargo before another press.
                pickup.ReplanAfterPress = true;
                GameObject initialCargo = ActionObservations.Held(chef);
                pickup.UseHeld = held => HeldPreparation(held, order) &&
                    (NeedsWork(order, source.Item) || !NeedsWork(order, held)) &&
                    (source.Supply || PreparationProgress(held) >= PreparationProgress(source.Item));
                // If an existing catch lost to a more advanced source, park it.
                // Only a new catch invalidates this scheduling decision.
                pickup.ReplanForHeld = held => !System.Object.ReferenceEquals(held, initialCargo) && UsefulCatch(held);
                yield return pickup;
                if (!pickup.Succeeded) { foreach (var step in Clear(pickup.Obstruction)) yield return step; yield break; }
                item = ActionObservations.Held(chef);
            }
            while (NeedsWork(order, item))
            {
                if (board == null)
                {
                    bool occupied;
                    board = Board(item, out occupied);
                    if (board == null) { foreach (var step in Park(item)) yield return step; yield break; }
                    var place = Reschedulable("place_at", board);
                    yield return place;
                    if (!place.Succeeded)
                    {
                        if (place.Obstruction == null) { foreach (var step in Park(item)) yield return step; }
                        else foreach (var step in Clear(place.Obstruction)) yield return step;
                        yield break;
                    }
                }
                // A ready portion may have arrived while we carried/placed this
                // raw food. Leave it on its board and dispatch the handoff first.
                // This is a safe action boundary, not a new per-frame world scan.
                if (ReadyPreparationWaiting()) yield break;
                var work = Reschedulable("work_at", board, "work_complete");
                GameObject initialWorkCargo = ActionObservations.Held(chef);
                work.ReplanForHeld = held => !System.Object.ReferenceEquals(held, initialWorkCargo) && UsefulCatch(held);
                yield return work;
                if (!work.Succeeded) { foreach (var step in Clear(work.Obstruction)) yield return step; yield break; }
                item = ActionObservations.Occupant(board);
                if (item == null) yield break;
                if (NeedsWork(order, item)) continue;
                // Keep a prepared successor on its board while the only pan is
                // busy. Re-enter with empty hands to collect the finished batch.
                if (Passive(order) && preparation.Cooking.Count != 0) yield break;
                var lift = Reschedulable("pick_up", board);
                lift.ReplanForHeld = UsefulCatch;
                yield return lift;
                if (!lift.Succeeded) yield break;
                item = ActionObservations.Held(chef); board = null;
            }
            if (item == null) yield break;
            if (Passive(order))
            {
                foreach (var step in LoadProcessor(lane, item)) yield return step;
            }
            else foreach (var step in DeliverPreparation(lane, item)) yield return step;
        }

        private IEnumerable<TaskStep> LoadProcessor(PreparationSchedule.Lane lane, GameObject item)
        {
            bool mixing = lane.Order.Preparation == "mixed";
            GameObject processor = Processor(item, mixing);
            if (processor == null)
            {
                LogisticsItem empty; GameObject equipment;
                if (!EquipmentPair(item, mixing, out empty, out equipment))
                {
                    lane.RetryAt = Time.realtimeSinceStartup + 1;
                    foreach (var step in Park(item)) yield return step;
                    yield break;
                }
                GameObject staging = Parking(item);
                if (staging == null) { foreach (var step in Park(item)) yield return step; yield break; }
                var put = Reschedulable("place_at", staging); yield return put;
                if (!put.Succeeded) yield break;
                var lift = Reschedulable("pick_up", empty.Source); yield return lift;
                if (!lift.Succeeded) yield break;
                var install = Reschedulable("place_at", equipment); yield return install;
                if (!install.Succeeded) { foreach (var step in Park(empty.Item)) yield return step; yield break; }
                // Reobserve staged food after this multi-step equipment change.
                yield break;
            }
            var add = Reschedulable("add_ingredient", processor); yield return add;
            if (!add.Succeeded) { foreach (var step in Park(item)) yield return step; yield break; }
            preparation.StartCooking(lane, processor);
            Component mixer = TaskNative.Component<ServerMixingStation>(processor);
            if (mixer != null && !ActionObservations.Read<bool>(mixer, "m_bMixerOn")) yield return Do("activate", processor);
            Component cooker = TaskNative.Component<ServerCookingStation>(processor);
            if (cooker != null && !ActionObservations.Read<bool>(cooker, "m_isTurnedOn") && processor.GetComponent<ClientInteractable>() != null)
                yield return Do("activate", processor);
        }

        private IEnumerable<TaskStep> CollectBatch(PreparationSchedule.Batch batch)
        {
            var serving = ServingUtensil(batch.Container, batch.Station);
            if (serving != null)
            {
                var pickup = GetServingUtensil(serving, batch.Container);
                pickup.ReplanOnBlock = true;
                pickup.AbandonBeforePress = () => !batch.Intact || child.RouteUnavailable;
                yield return pickup;
                if (!pickup.Succeeded || !batch.Intact) yield break;
            }
            var collect = Reschedulable(serving == null ? "pick_up" : "collect_contents", batch.Station);
            collect.AbandonBeforePress = () => !batch.Intact || child.TransferUnavailable;
            yield return collect;
            if (!collect.Succeeded) { batch.Lane.RetryAt = Time.realtimeSinceStartup + 1; yield break; }
            preparation.Release(batch);
            foreach (var step in DeliverPreparation(batch.Lane, ActionObservations.Held(chef))) yield return step;
        }

        private IEnumerable<TaskStep> ResumeHandoff(PrepJob job)
        {
            GameObject item = job.Lane.ParkedMeal;
            var pickup = Reschedulable("pick_up", job.Source.Source);
            yield return pickup;
            if (!pickup.Succeeded || !System.Object.ReferenceEquals(item, ActionObservations.Held(chef)))
            { job.Lane.RetryAt = Time.realtimeSinceStartup + 1; yield break; }
            job.Lane.ParkedMeal = null;
            foreach (var step in DeliverPreparation(job.Lane, item)) yield return step;
        }

        private IEnumerable<TaskStep> DeliverPreparation(PreparationSchedule.Lane lane, GameObject item)
        {
            // A safe but temporarily impossible handoff (e.g. nonthrowable pan
            // across a gap) must not monopolize the request. Yield only to real
            // work and only with a safe place for this exact cargo. Never
            // interrupt a pressed button or an already released throw.
            bool deferred = false;
            float checkAt = Time.realtimeSinceStartup + 4;
            Func<bool> defer = delegate {
                if (deferred) return true;
                if (Time.realtimeSinceStartup < checkAt || lane.ParkedMeal != null ||
                    !System.Object.ReferenceEquals(item, ActionObservations.Held(chef))) return false;
                checkAt = Time.realtimeSinceStartup + 1;
                if (preparation.Cooking.Count == 0 && preparation.Lanes.Length == 1) return false;
                inventory.Refresh(chef);
                bool urgent = false;
                foreach (var batch in preparation.Cooking)
                    if (batch.Intact && TaskNative.Remaining(batch.Container, batch.Mixing) < 5) urgent = true;
                BoardWait unused = BoardWait.None;
                deferred = (urgent || SelectPreparation(Single.MaxValue, ref unused, lane) != null) && Parking(item) != null;
                return deferred;
            };
            bool dispatched = false;
            Action<GameObject, int> dispatch = (cargo, player) => {
                dispatched = true; lane.InTransit++;
                DispatchDelivery(cargo, player, () => cargo != null && cargo.activeInHierarchy &&
                    (!Passive(lane.Order) || !LogisticsInventory.Empty(cargo) && !TaskNative.Burnt(cargo)),
                    received => { lane.InTransit--; if (received) completed = preparation.Complete(lane, cargo); });
            };
            foreach (var step in Give(item, request.Player, dispatch, true, defer)) yield return step;
            if (dispatched) yield break;
            if (deliveredItems.Contains(item)) completed = preparation.Complete(lane, item);
            else if (deferred && System.Object.ReferenceEquals(item, ActionObservations.Held(chef)))
            {
                foreach (var step in Park(item)) yield return step;
                lane.ParkedMeal = item;
                lane.RetryAt = Time.realtimeSinceStartup + 4;
            }
        }
    }
}
