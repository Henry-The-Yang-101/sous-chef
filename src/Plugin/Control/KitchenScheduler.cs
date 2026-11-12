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
        private readonly DeliveryHistory kitchenDeliveries = new DeliveryHistory();
        private KitchenJob kitchenActive;
        private float nextKitchenSchedule;
        private bool kitchenHeld;
        private readonly HashSet<string> kitchenStartedAtHold = new HashSet<string>();
        private void SetKitchenHold(bool hold, int duties)
        {
            if (hold && !kitchenHeld)
                foreach (var job in kitchenJobs) if (job.Started && !job.Duty) kitchenStartedAtHold.Add(job.Command.Id);
            if (!hold) { kitchenStartedAtHold.Clear(); kitchenError = null; }
            kitchenHeld = hold; kitchenDutyMask = duties;
        }
        private int kitchenDutyMask = 15;
        private bool kitchenEnabled;
        private string kitchenError;
        private int kitchenOwner;
        private readonly Dictionary<string, int> kitchenDutyCompletions = new Dictionary<string, int>();
        private static readonly string[] KitchenDuties = { "deliver_completed_dishes", "monitor_equipment", "wash_dishes", "put_out_fire" };
        private bool DutyEnabled(KitchenJob job) { int index = Array.IndexOf(KitchenDuties, job.Command.Task.Action); return !job.Duty || index >= 0 && (kitchenDutyMask & (1 << index)) != 0 && !kitchenJobs.Exists(other => !other.Duty && !other.Receipt.Terminal && other.Command.Task.Action == job.Command.Task.Action); }

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
            kitchenEnabled = true; kitchenOwner = owner;
            receipt.State = "waiting"; receipt.Reason = "admitted_to_kitchen";
            nextKitchenSchedule = 0;
            return null;
        }
        private bool ForgetKitchen(string id, string reason)
        {
            bool found = false;
            foreach (var job in kitchenJobs)
            {
                if (job.Duty || job.Command.Id != id) continue;
                found = true;
                if (job == kitchenActive) { job.Task.Cancel(reason); kitchenActive = null; }
                else if (job.Started && job.Task.Run.Running) job.Task.AbandonSuspended(reason);
                job.Receipt.State = "cancelled"; job.Receipt.Reason = reason;
            }
            kitchenJobs.RemoveAll(job => !job.Duty && job.Command.Id == id);
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
            kitchenStartedAtHold.Clear(); kitchenDutyCompletions.Clear(); kitchenJobs.Clear(); kitchenActive = null; kitchenDeliveries.Clear(); kitchenHeld = false; kitchenDutyMask = 15; kitchenEnabled = false; kitchenError = null;
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
                    if (done) { if (job.Task.Run.State == "succeeded") complete++; else failure = job.Task.Run.Reason ?? job.Task.Run.State; }
                    running |= job == kitchenActive && (job.Offer == null || job.Offer.Ready);
                    string state = done ? "done" : job == kitchenActive && (job.Offer == null || job.Offer.Ready) ? "running" : job.Offer != null && job.Offer.Ready ? "ready" : "blocked";
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
                    foreach (Json.Record row in rows)
                        if ((string)row["state"] != "done") { row["state"] = "done"; row["outcome"] = "cancelled"; }
                    foreach (var job in group)
                        if (job.Started && job.Task.Run.Running)
                        { if (job == kitchenActive) job.Task.Cancel(failure); else job.Task.AbandonSuspended(failure); }
                }
            }
            foreach (var job in kitchenJobs)
                if (job.Duty && job.Started && !job.Task.Run.Running && job.Task.Run.State == "succeeded")
                {
                    int count; kitchenDutyCompletions.TryGetValue(job.Command.Task.Action, out count);
                    kitchenDutyCompletions[job.Command.Task.Action] = count + 1;
                }
            kitchenJobs.RemoveAll(job => !job.Duty && job.Receipt.Terminal || job.Duty && job.Started && !job.Task.Run.Running);
        }
        private void TickKitchenSafely()
        {
            if (kitchenError != null) return;
            try { TickKitchen(); }
            catch (Exception error)
            {
                kitchenError = error.ToString(); kitchenHeld = true; Buttons(0);
                if (navigation.Action.State == "navigating" && !navigation.NativeTeleportInProgress(target)) navigation.Stop("scheduler_observation_unavailable");
            }
        }
        private void TickKitchen()
        {
            if (Time.realtimeSinceStartup < nextKitchenSchedule) return;
            nextKitchenSchedule = Time.realtimeSinceStartup + .2f;
            kitchenDeliveries.Observe();
            PublishKitchen();
            if (kitchenActive != null && !kitchenActive.Task.Run.Running) kitchenActive = null;
            if (kitchenJobs.Count == 0 && !kitchenEnabled) return;
            // Manual actions keep their existing lease. Scheduled input never races them.
            if (kitchenActive == null && (tasks.Run.Running || interactions.Run.Running || logistics.Run.Running || navigation.Action.State != "idle")) return;
            bool requested = kitchenEnabled;
            if (requested)
                foreach (string duty in KitchenDuties)
                    if (!kitchenJobs.Exists(job => job.Duty && job.Command.Task.Action == duty))
                        kitchenJobs.Add(NewKitchenJob(new ControlCommand { Id = "duty_" + duty, Operation = "task", Indefinite = true,
                            Task = new TaskRequest { Action = duty } }, null, kitchenOwner, 1, true));
            var demandJobs = new List<TaskActions>();
            foreach (var job in kitchenJobs) if (!job.Duty && (!job.Started || job.Task.Run.Running)) demandJobs.Add(job.Task);
            TaskActions.AssignKitchenDemand(target, demandJobs);
            KitchenJob best = null;
            var unstartedKinds = new HashSet<string>();
            foreach (var job in kitchenJobs)
            {
                if (job.Started && !job.Task.Run.Running) continue;
                if (!job.Started && job.Command.Task.Action == "prepare_near_player" && job.Command.Task.Items.Count == 1)
                {
                    var item = job.Command.Task.Items[0];
                    string kind = job.Command.Id + ":" + item.Selector + ":" + item.Preparation;
                    if (!unstartedKinds.Add(kind))
                    { job.Offer = new WorkOffer { Ready = false, Reason = "waiting_for_portion_admission", Stage = "prepare_ingredient" }; continue; }
                }
                if ((!requested && !job.Started) || !DutyEnabled(job)) continue;
                if (job.Suspended) job.Task.ObserveSuspended();
                job.Offer = job.Task.SchedulingOffer(target, job.Command.Task, job == kitchenActive);
                job.Offer.Age = Time.realtimeSinceStartup - job.ReadySince;
                if (job.Urgent && job.Offer.Class > 0) job.Offer.Class = 1;
                if (job.Duty && job.Command.Task.Action == "wash_dishes" && job.Offer.Class > 2) job.Offer.Ready = false;
            }
            // Propagate urgency through actual resource owners. A blocked job does
            // not compete for input; the work releasing its prerequisite does.
            for (int pass = 0; pass < Math.Min(16, kitchenJobs.Count); pass++)
            {
                bool changed = false;
                foreach (var waiter in kitchenJobs)
                    if (waiter.Offer != null && !waiter.Offer.Ready)
                        foreach (var owner in kitchenJobs)
                            if (owner != waiter && owner.Offer != null && owner.Offer.Ready &&
                                (waiter.Offer.Class < owner.Offer.Class || waiter.Offer.Class == owner.Offer.Class && waiter.Offer.Slack < owner.Offer.Slack) &&
                                waiter.Task.WaitingForOwner(owner.Task, waiter.Offer.Reason))
                            {
                                owner.Offer.Class = Math.Min(owner.Offer.Class, waiter.Offer.Class);
                                owner.Offer.Slack = Math.Min(owner.Offer.Slack, waiter.Offer.Slack);
                                owner.Offer.Reason = "release_urgent_dependency"; changed = true;
                            }
                if (!changed) break;
            }
            foreach (var job in kitchenJobs)
            {
                if (job.Offer == null || job.Started && !job.Task.Run.Running || !DutyEnabled(job) || kitchenHeld && job != kitchenActive && !kitchenStartedAtHold.Contains(job.Command.Id)) continue;
                if (job.Offer.PreferTo(best == null ? null : best.Offer)) best = job;
            }
            if (kitchenActive != null && !DutyEnabled(kitchenActive))
            {
                if (!kitchenActive.Task.Suspend()) return;
                kitchenActive.Task.AbandonSuspended("explicit_duty_takes_precedence");
                lease.Revoke("duty_superseded"); Restore(); kitchenActive = null;
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
            var completions = Json.Object();
            foreach (var pair in kitchenDutyCompletions) completions[pair.Key] = pair.Value;
            var receipts = new List<object>(); var seen = new HashSet<string>();
            foreach (var job in kitchenJobs)
                if (job.Receipt != null && seen.Add(job.Receipt.Id)) receipts.Add(job.Receipt.Describe());
            return Json.Object("held", kitchenHeld, "duties", kitchenDutyMask, "enabled", kitchenEnabled, "error", kitchenError, "dutyCompletions", completions, "activeRequestId", kitchenActive == null || kitchenActive.Duty ? null : kitchenActive.Command.Id,
                "duty", kitchenActive != null && kitchenActive.Duty ? kitchenActive.Command.Task.Action : null, "receipts", receipts);
        }
    }
}
