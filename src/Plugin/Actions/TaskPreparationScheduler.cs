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
                { deliveredItems.Add(item); completed = preparation.Complete(lane); lane.ParkedMeal = null; }
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
                else availableSeconds = Math.Min(availableSeconds, TaskNative.Remaining(batch.Container, batch.Mixing));
            }
            return ready;
        }

        private PrepJob SelectPreparation(float availableSeconds, ref BoardWait boardWait, PreparationSchedule.Lane exclude = null)
        {
            GameObject held = ActionObservations.Held(chef); PrepJob first = null;
            for (int n = 0; n < preparation.Lanes.Length; n++)
            {
                var lane = preparation.At(n);
                if (lane == exclude || Time.realtimeSinceStartup < lane.RetryAt) continue;
                if (lane.ParkedMeal != null)
                {
                    foreach (var entry in inventory.Items)
                        if (System.Object.ReferenceEquals(lane.ParkedMeal, entry.Item) && Obtainable(entry, "move_item"))
                            return new PrepJob { Lane = lane, Source = entry, Parked = true };
                    continue;
                }
                if (!lane.WantsMore) continue;
                var source = StartingSource(lane.Order, ref boardWait);
                if (source == null || availableSeconds != Single.MaxValue && availableSeconds <= PreparationSeconds(lane.Order, source)) continue;
                if (Passive(lane.Order) && !CanPrepareForProcessor(lane, source)) continue;
                var job = new PrepJob { Lane = lane, Source = source };
                if (held == null) return job;
                if (System.Object.ReferenceEquals(held, source.Item))
                    return first != null && ReadyToGive(first) && !ReadyToGive(job) ? first : job;
                if (first == null) first = job;
            }
            return first;
        }

        private static bool ReadyToGive(PrepJob job)
        {
            return !job.Source.Supply && !Passive(job.Lane.Order) && !NeedsWork(job.Lane.Order, job.Source.Item);
        }
        private bool CanPrepareForProcessor(PreparationSchedule.Lane lane, LogisticsItem source)
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
                return lane.InFlight != 0 && NeedsWork(lane.Order, source.Item);
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
                GameObject initialCargo = ActionObservations.Held(chef);
                pickup.UseHeld = held => HeldPreparation(held, order) &&
                    (NeedsWork(order, source.Item) || !NeedsWork(order, held));
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
                var work = Reschedulable("work_at", board, "work_complete");
                work.ReplanForHeld = UsefulCatch;
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
                    received => { lane.InTransit--; if (received) completed = preparation.Complete(lane); });
            };
            foreach (var step in Give(item, request.Player, dispatch, true, defer)) yield return step;
            if (dispatched) yield break;
            if (deliveredItems.Contains(item)) completed = preparation.Complete(lane);
            else if (deferred && System.Object.ReferenceEquals(item, ActionObservations.Held(chef)))
            {
                foreach (var step in Park(item)) yield return step;
                lane.ParkedMeal = item;
                lane.RetryAt = Time.realtimeSinceStartup + 4;
            }
        }
    }
}
