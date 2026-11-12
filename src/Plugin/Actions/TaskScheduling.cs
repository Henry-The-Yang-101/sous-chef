using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        internal Func<TaskActions, GameObject, bool> ReservedByOther { get; set; }
        internal Func<TaskActions, bool, bool> ProcessorBuffered { get; set; }
        private readonly HashSet<GameObject> ownedFood = new HashSet<GameObject>();
        private readonly HashSet<GameObject> ownedSinks = new HashSet<GameObject>();
        private readonly HashSet<GameObject> ownedBoards = new HashSet<GameObject>();
        internal bool Owns(GameObject item)
        {
            if (item == null) return false;
            if (ownedFood.Contains(item)) return true;
            ownedSinks.RemoveWhere(sink => sink == null || ActionObservations.DirtyCount(sink) == 0);
            if (ownedSinks.Contains(item)) return true;
            foreach (var board in new List<GameObject>(ownedBoards))
                if (board == null || !(childStarted && step != null && step.Target == board && step.Phase == "work_at") &&
                    (ActionObservations.Occupant(board) == null || !ownedFood.Contains(ActionObservations.Occupant(board)))) ownedBoards.Remove(board);
            foreach (var board in ownedBoards)
                if (board != null && (board == item || ActionObservations.Occupant(board) == item)) return true;
            if (preparation != null)
                foreach (var batch in preparation.Cooking) if (batch.Intact && (batch.Container == item || batch.Station == item)) return true;
            if (assembly != null)
            {
                if (assembly.Base == item) return true;
                foreach (var stage in assembly.Stages) if (stage.Vessel == item) return true;
            }
            return false;
        }
        private bool Foreign(GameObject item) { return item != null && ReservedByOther != null && ReservedByOther(this, item); }
        internal void ConfigureScheduling(PlayerControls actor, TaskRequest desired) { chef = actor; request = desired; }
        internal void ShareDeliveries(DeliveryHistory deliveries) { sharedDeliveries = deliveries; }
        internal bool AtSchedulingBoundary { get { return restoration == null && handsPlan == null && (!childStarted || child.Run.Operation == "work_at" || child.CanRetry && (child.RouteUnavailable || child.NavigationStalled)); } }
        private bool AuthorizeScheduledStep(TaskStep next)
        {
            if (ReservedByOther == null) return true;
            var occupant = ActionObservations.Occupant(next.Target);
            if (Foreign(next.Target) || Foreign(occupant)) return false;
            // A selected source can have been delivered by another job before
            // this job resumes. Revalidate the shared ledger at the input edge.
            if (request.Action == "prepare_near_player" && next.Phase == "pick_up" && PreviouslyDelivered(occupant ?? next.Target)) return false;
            if (next.Target != null && next.Target.GetComponent<ServerWashingStation>() != null &&
                (next.Phase == "work_at" || next.Phase == "place_at" && TaskNative.Dirty(ActionObservations.Held(chef)))) ownedSinks.Add(next.Target);
            if (next.Phase == "work_at" && next.Target != null && next.Target.GetComponent<ServerWorkstation>() != null)
            {
                var item = ActionObservations.Occupant(next.Target);
                if (item != null) { ownedFood.Add(item); ownedBoards.Add(next.Target); }
            }
            return true;
        }
        private float schedulingRetryAt;
        private void ObserveWorkOwnership(TaskStep finished)
        {
            if (ReservedByOther == null || finished == null || finished.Target == null) return;
            if (finished.Phase == "pick_up")
            {
                var held = ActionObservations.Held(chef);
                if (held != null && !Foreign(held)) ownedFood.Add(held);
                ownedBoards.Remove(finished.Target);
            }
            if (finished.Phase == "work_at" && finished.Target.GetComponent<ServerWorkstation>() != null)
            {
                ownedBoards.Add(finished.Target);
                var output = ActionObservations.Occupant(finished.Target);
                if (output != null) ownedFood.Add(output);
            }
        }
        internal WorkOffer SchedulingOffer(PlayerControls actor, TaskRequest desired, bool selected)
        {
            chef = actor;
            if (request == null) request = desired;
            var offer = new WorkOffer { Stage = step == null ? desired.Action : step.Phase,
                Reason = waitingReason ?? "requested_work", Cost = selected ? 0 : 2 };
            inventory.BeginObservation();
            try
            {
                inventory.Refresh(chef);
                if (childStarted && child.CanRetry && (child.RouteUnavailable || child.NavigationStalled))
                {
                    offer.Ready = Time.realtimeSinceStartup >= schedulingRetryAt;
                    if (offer.Ready) schedulingRetryAt = Time.realtimeSinceStartup + 2;
                    offer.Reason = "waiting_for_route"; return offer;
                }
                if (Run.Running && restoration != null) { offer.Remaining = 2; return offer; }
                if (desired.Action == "assemble_dish")
                {
                    var order = AssemblyOrders.Observe().Find(value => value.Id == desired.Assembly.Order);
                    if (order == null) { offer.Class = 0; offer.Reason = "expired_exact_ticket"; return offer; }
                    offer.Slack = order.Expires ? order.Ticket.Remaining - 15 : Single.MaxValue;
                    offer.Class = 2;
                }
                // Every retained task observes its own hot batches even while another
                // job owns the chef. Collection remains in the owning iterator.
                float heat = Single.MaxValue;
                foreach (var station in navigation.Targets())
                {
                    if (!TaskNative.Equipment(station)) continue;
                    var food = ActionObservations.Receiver(station);
                    if (food == null || TaskNative.Burnt(food) || LogisticsInventory.Empty(food)) continue;
                    bool owns = Owns(food);
                    bool monitor = desired.Action == "monitor_equipment" || desired.Action == "take_ready";
                    if (!owns && (!monitor || Foreign(food) || Foreign(station))) continue;
                    bool mixing = TaskNative.IsMixer(station);
                    if (TaskNative.State(food, mixing) == null) continue;
                    float trip = ApproachCost(station) / Math.Max(.1f, chef.Movement.RunSpeed) + 4;
                    float slack = TaskNative.Remaining(food, mixing, true) - trip;
                    if (TaskNative.Ready(food, mixing) || TaskNative.Remaining(food, mixing) <= trip)
                        heat = Math.Min(heat, slack);
                }
                if (heat != Single.MaxValue)
                {
                    offer.Class = heat < 5 ? 0 : 1; offer.Slack = heat;
                    offer.Remaining = 3; offer.Reason = "collect_processing_output"; return offer;
                }
                if (desired.Action == "assemble_dish" && assembly != null && !childStarted)
                {
                    RefreshAssemblyResources();
                    if (System.Object.ReferenceEquals(assembly.Base, null)) assembly.Base = SelectAssemblyBase();
                    offer.Ready = SelectAssemblyWork() != null;
                    offer.Reason = offer.Ready ? "advance_dish_dependency" : assembly.Waiting ?? "waiting_for_dish_dependency";
                }
                if (desired.Action == "prepare_near_player")
                {
                    offer.Slack = assignedOrderSeconds == Single.MaxValue ? assignedOrderSeconds : assignedOrderSeconds - offer.Remaining - 8;
                    offer.Class = offer.Slack == Single.MaxValue ? 3 : 2;
                    if (preparation == null) preparation = new PreparationSchedule(desired);
                    float available; ObserveCooking(out available);
                    BoardWait wait = BoardWait.None;
                    var job = SelectPreparation(available, ref wait);
                    if (job == null)
                    {
                        offer.Ready = childStarted && waitingReason == null;
                        offer.Reason = wait != BoardWait.None ? "waiting_for_chopping_board" : "waiting_for_ingredient_or_processing";
                    }
                    else
                    {
                        offer.Remaining = ReadyToGive(job) ? 3 : PreparationSeconds(job.Lane.Order, job.Source);
                        offer.PassiveSeconds = Passive(job.Lane.Order) ? ProcessingBudget(job.Lane.Order.Preparation == "mixed") : 0;
                        offer.Slack = PreparationOrderSlack(job.Lane.Order, offer.Remaining + offer.PassiveSeconds);
                        offer.Class = offer.Slack == Single.MaxValue ? 3 : 2;
                        offer.Reason = ReadyToGive(job) ? "finish_prepared_portion" : "advance_requested_portion";
                    }
                }
                else if (desired.Action == "deliver_completed_dishes")
                {
                    GameObject destination;
                    var dish = PlatedDish(out destination);
                    offer.Ready = dish != null;
                    if (dish != null)
                    {
                        float expiry; DishExpiry(dish.Item, destination, out expiry);
                        offer.Remaining = (dish.Distance + inventory.DistanceTo(destination)) / Math.Max(.1f, chef.Movement.RunSpeed) + 2;
                        offer.Slack = expiry == Single.MaxValue ? expiry : expiry - offer.Remaining;
                        offer.Class = offer.Slack < 5 ? 0 : 1;
                    }
                    offer.Reason = dish == null ? "waiting_for_completed_dish" : "serve_completed_dish";
                }
                else if (desired.Action == "monitor_equipment" || desired.Action == "take_ready")
                { offer.Ready = childStarted; offer.Reason = "waiting_for_processing_output"; }
                else if (desired.Action == "put_out_fire")
                { bool fire = FireAvailable(); offer.Ready = fire || childStarted; offer.Class = fire ? 0 : 3; offer.Slack = 0; offer.Reason = "extinguish_fire"; }
                else if (desired.Action == "wash_dishes")
                {
                    var sink = Sink();
                    offer.Ready = sink != null && !Foreign(sink) && HasDirtyDishes(sink) || sink == null && WashingSupported() && DirtySources().Count > 0;
                    offer.Remaining = 6; offer.Reason = "replenish_clean_plates";
                    bool clean = false;
                    foreach (var item in inventory.Items) if (item.CleanPlate && !Foreign(item.Item)) clean = true;
                    if (!clean)
                        foreach (var order in AssemblyOrders.Observe())
                            if (order.Expires) { offer.Class = 2; offer.Slack = Math.Min(offer.Slack, order.Ticket.Remaining - 12); }
                }
                else if (waitingReason != null && !childStarted)
                { offer.Ready = step == null || step.Ready == null || step.Ready(); }
                if (offer.Class == 2)
                { offer.Feasible = offer.Slack >= -2; if (offer.Feasible && offer.Slack < 5) offer.Class = 0; }
                return offer;
            }
            catch (Exception) { offer.Ready = false; offer.Reason = "scheduling_observation_unavailable"; return offer; }
            finally { inventory.EndObservation(); }
        }
        internal bool HasBufferedPreparation(bool mixing)
        {
            if (request == null || request.Action != "prepare_near_player" || preparation == null || !Run.Running) return false;
            foreach (var lane in preparation.Lanes)
                if (Passive(lane.Order) && (lane.Order.Preparation == "mixed") == mixing && lane.WantsMore && ownedFood.Count > 0) return true;
            return false;
        }
        internal bool WaitingForOwner(TaskActions owner, string reason)
        {
            if (step != null && step.Target != null && (owner.Owns(step.Target) || owner.Owns(ActionObservations.Occupant(step.Target)))) return true;
            if (reason == "waiting_for_chopping_board")
                foreach (var station in navigation.Targets())
                    if (station.GetComponent<ServerWorkstation>() != null && owner.Owns(station)) return true;
            if (assembly != null && assembly.Base == null)
                foreach (var entry in inventory.Items)
                    if (entry.CleanPlate && owner.Owns(entry.Item)) return true;
            return false;
        }
        private float ProcessingBudget(bool mixing)
        {
            float duration = Single.MaxValue;
            foreach (var item in inventory.Items)
            {
                var handler = TaskNative.ProcessingHandler(item.Item, mixing);
                if (handler == null) continue;
                var property = handler.GetType().GetProperty(mixing ? "AccessMixingTime" : "AccessCookingTime");
                if (property == null) continue;
                float observed = Convert.ToSingle(property.GetValue(handler, null));
                if (observed > 0) duration = Math.Min(duration, observed);
            }
            // No observed duration is unknown, not an invented recipe-specific time.
            return duration == Single.MaxValue ? 0 : duration;
        }
        private float PreparationOrderSlack(PreparationRequest desired, float remaining)
        {
            return assignedOrderSeconds == Single.MaxValue ? assignedOrderSeconds : assignedOrderSeconds - remaining - 8;
        }

    }
}
