using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Actions;
using UnityEngine;

namespace SousChef.Plugin.Control
{
    internal sealed partial class Player2Control
    {
        private sealed class KitchenJob
        {
            public ControlCommand Command;
            public ReceiptBook.Entry Receipt;
            public TaskActions Task;
            public InteractionActions Child;
            public int Owner, Portion;
            public bool Started, Suspended, Urgent, Duty;
            public float ReadySince, LastRan;
            public WorkOffer Offer;
        }
        private readonly List<KitchenJob> kitchenJobs = new List<KitchenJob>();
        private readonly HashSet<GameObject> kitchenDeliveries = new HashSet<GameObject>();
        private KitchenJob kitchenActive;
        private float nextKitchenSchedule;
        private bool kitchenHeld;

        private KitchenJob NewKitchenJob(ControlCommand command, ReceiptBook.Entry receipt, int owner, int portion, bool duty = false)
        {
            var child = new InteractionActions(navigation, Buttons, FinishInteraction);
            var task = new TaskActions(navigation, child, Buttons, FinishLogistics);
            task.ReservedByOther = (requester, item) => {
                foreach (var peer in kitchenJobs)
                    if (peer.Task != requester && (!peer.Started || peer.Task.Run.Running) && peer.Task.Owns(item)) return true;
                return false;
            };
            task.ProcessorBuffered = (requester, mixing) => {
                foreach (var peer in kitchenJobs) if (peer.Task != requester && peer.Task.HasBufferedPreparation(mixing)) return true;
                return false;
            };
            task.ShareDeliveries(kitchenDeliveries);
            task.ConfigureScheduling(target, command.Task);
            return new KitchenJob { Command = command, Receipt = receipt, Owner = owner, Portion = portion,
                Task = task, Child = child, ReadySince = Time.realtimeSinceStartup, Urgent = command.Priority, Duty = duty };
        }
        private string EnqueueKitchen(int owner, ControlCommand command, ReceiptBook.Entry receipt)
        {
            if (receipt == null || command.Task == null) return "scheduled_task_required";
            var requests = new List<TaskRequest>(); var desired = command.Task;
            if (desired.Action == "prepare_near_player" && desired.Repetitions != 0)
                for (int repeat = 0; repeat < desired.Repetitions; repeat++)
                    foreach (var item in desired.Items)
                        for (int n = 0; n < item.Quantity; n++)
                        {
                            var one = new TaskRequest { Action = desired.Action, Player = desired.Player };
                            one.Items.Add(new PreparationRequest { Selector = item.Selector, Preparation = item.Preparation, Quantity = 1 });
                            requests.Add(one);
                        }
            else requests.Add(desired);
            if (requests.Count + kitchenJobs.Count > 1024) return "kitchen_job_capacity";
            int portion = 0;
            foreach (var request in requests)
                kitchenJobs.Add(NewKitchenJob(new ControlCommand { Id = command.Id, Operation = "task", Task = request,
                    Priority = command.Priority, Indefinite = true }, receipt, owner, ++portion));
            receipt.State = "waiting"; receipt.Reason = "admitted_to_kitchen";
            nextKitchenSchedule = 0;
            return null;
        }
        private bool ForgetKitchen(string id, string reason)
        {
            bool found = false;
            foreach (var job in kitchenJobs)
            {
                if (job.Command.Id != id) continue;
                found = true;
                if (job == kitchenActive) { job.Task.Cancel(reason); kitchenActive = null; }
                else if (job.Started && job.Task.Run.Running) job.Task.AbandonSuspended(reason);
                job.Receipt.State = "cancelled"; job.Receipt.Reason = reason;
            }
            kitchenJobs.RemoveAll(job => job.Command.Id == id);
            return found;
        }
        private void ClearKitchen(string reason, bool preserve)
        {
            if (preserve)
            {
                if (kitchenActive != null && !kitchenActive.Duty) ForgetKitchen(kitchenActive.Command.Id, reason);
                return;
            }
            foreach (var job in kitchenJobs)
            {
                if (job.Started && job.Task.Run.Running)
                { if (job == kitchenActive) job.Task.Cancel(reason); else job.Task.AbandonSuspended(reason); }
                if (job.Receipt != null) { job.Receipt.State = "cancelled"; job.Receipt.Reason = reason; }
            }
            kitchenJobs.Clear(); kitchenActive = null; kitchenDeliveries.Clear(); kitchenHeld = false;
        }
        private void PublishKitchen()
        {
            var groups = new Dictionary<string, List<KitchenJob>>();
            foreach (var job in kitchenJobs)
            {
                if (job.Duty) continue;
                List<KitchenJob> group;
                if (!groups.TryGetValue(job.Command.Id, out group)) groups.Add(job.Command.Id, group = new List<KitchenJob>());
                group.Add(job);
            }
            foreach (var group in groups.Values)
            {
                var receipt = group[0].Receipt; var rows = new List<object>();
                int complete = 0; string failure = receipt.Terminal && receipt.State != "succeeded" ? receipt.Reason : null; bool running = false;
                foreach (var job in group)
                {
                    bool done = job.Started && !job.Task.Run.Running;
                    if (done) { complete++; if (job.Task.Run.State != "succeeded") failure = job.Task.Run.Reason ?? job.Task.Run.State; }
                    running |= job == kitchenActive;
                    string state = done ? "done" : job == kitchenActive ? "running" : job.Offer != null && job.Offer.Ready ? "ready" : "blocked";
                    rows.Add(Json.Object("id", receipt.Id + ":" + job.Portion, "action", job.Command.Task.Action,
                        "state", state, "outcome", done ? job.Task.Run.State : null,
                        "scheduling", job.Offer == null ? null : job.Offer.Describe(state),
                        "progress", job.Started ? job.Task.Describe() : null));
                }
                receipt.State = failure != null ? "failed" : complete == group.Count ? "succeeded" : running ? "running" : "waiting";
                receipt.Reason = failure ?? (complete == group.Count ? "task_complete" : "kitchen_scheduling");
                receipt.Evidence = Json.Object("jobs", rows, "completedPortions", complete, "totalPortions", group.Count);
                if (failure != null)
                {
                    kitchenHeld = true;
                    foreach (var job in group)
                        if (job.Started && job.Task.Run.Running)
                        { if (job == kitchenActive) job.Task.Cancel(failure); else job.Task.AbandonSuspended(failure); }
                }
            }
            kitchenJobs.RemoveAll(job => !job.Duty && job.Receipt.Terminal || job.Duty && job.Started && !job.Task.Run.Running);
        }
        private void TickKitchen()
        {
            PublishKitchen();
            if (kitchenActive != null && !kitchenActive.Task.Run.Running) kitchenActive = null;
            if (kitchenJobs.Count == 0) return;
            // Manual actions keep their existing lease. Scheduled input never races them.
            if (kitchenActive == null && (tasks.Run.Running || interactions.Run.Running || logistics.Run.Running || navigation.Action.State != "idle")) return;
            if (Time.realtimeSinceStartup < nextKitchenSchedule) return;
            nextKitchenSchedule = Time.realtimeSinceStartup + .2f;
            bool requested = kitchenJobs.Exists(job => !job.Duty && !job.Receipt.Terminal);
            if (requested)
                foreach (string duty in new[] { "deliver_completed_dishes", "monitor_equipment", "wash_dishes", "put_out_fire" })
                    if (!kitchenJobs.Exists(job => job.Duty && job.Command.Task.Action == duty))
                        kitchenJobs.Add(NewKitchenJob(new ControlCommand { Id = "duty_" + duty, Operation = "task", Indefinite = true,
                            Task = new TaskRequest { Action = duty } }, null, kitchenJobs[0].Owner, 1, true));
            var demandJobs = new List<TaskActions>();
            foreach (var job in kitchenJobs) if (!job.Duty && (!job.Started || job.Task.Run.Running)) demandJobs.Add(job.Task);
            TaskActions.AssignKitchenDemand(target, demandJobs);
            KitchenJob best = null;
            foreach (var job in kitchenJobs)
            {
                if (job.Started && !job.Task.Run.Running) continue;
                if (!requested && !job.Started) continue;
                if (job.Suspended) job.Task.ObserveSuspended();
                job.Offer = job.Task.SchedulingOffer(target, job.Command.Task, job == kitchenActive);
                job.Offer.Age = Time.realtimeSinceStartup - job.ReadySince;
                if (job.Urgent && job.Offer.Class > 0) job.Offer.Class = 1;
                if (job.Duty && job.Command.Task.Action == "wash_dishes" && job.Offer.Class > 2) job.Offer.Ready = false;
                if (kitchenHeld && job != kitchenActive) continue;
                if (job.Offer.PreferTo(best == null ? null : best.Offer)) best = job;
            }
            if (best == null || best == kitchenActive) return;
            if (kitchenActive != null)
            {
                // Ordinary switches happen between verified stages. Time-critical
                // work can interrupt chopping/washing using the existing checkpoint.
                bool urgent = best.Offer.Class <= 1 && (kitchenActive.Offer == null || best.Offer.Class < kitchenActive.Offer.Class);
                if (!urgent && (!kitchenActive.Task.AtSchedulingBoundary || Time.realtimeSinceStartup - kitchenActive.LastRan < 1)) return;
                if (!kitchenActive.Task.Suspend()) return;
                kitchenActive.Suspended = true; lease.Revoke("scheduled_yield"); Restore();
                kitchenActive = null;
            }
            if (!lease.Claim(best.Owner, Now, true)) return;
            tasks = best.Task; interactions = best.Child;
            logistics = new LogisticsActions(navigation, interactions, Buttons, FinishLogistics);
            Restore(); Install(); navigationSequence = 0; lastWasTask = true; lastWasLogistics = false;
            if (best.Started) tasks.Resume();
            else
            {
                string error = tasks.Start(target, best.Owner, best.Command);
                if (error != null)
                {
                    lease.Revoke(error); Restore();
                    if (error == "assembly_order_unavailable" || error == "assembly_resource_unavailable")
                    { best.Receipt.State = "failed"; best.Receipt.Reason = error; kitchenHeld = true; }
                    return;
                }
                best.Started = true;
            }
            best.Suspended = false; best.LastRan = Time.realtimeSinceStartup; best.ReadySince = best.LastRan;
            kitchenActive = best; activeReceipt = null;
            PublishKitchen();
        }
        private object DescribeKitchen()
        {
            var receipts = new List<object>(); var seen = new HashSet<string>();
            foreach (var job in kitchenJobs)
                if (job.Receipt != null && seen.Add(job.Receipt.Id)) receipts.Add(job.Receipt.Describe());
            return Json.Object("held", kitchenHeld, "activeRequestId", kitchenActive == null || kitchenActive.Duty ? null : kitchenActive.Command.Id,
                "duty", kitchenActive != null && kitchenActive.Duty ? kitchenActive.Command.Task.Action : null, "receipts", receipts);
        }
    }
}
