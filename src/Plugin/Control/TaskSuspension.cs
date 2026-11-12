using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Actions;

namespace SousChef.Plugin.Control
{
    internal sealed partial class Player2Control
    {
        private sealed class SuspendedTask
        {
            public TaskActions Task;
            public InteractionActions Child;
            public ReceiptBook.Entry Receipt;
        }
        private readonly Dictionary<string, SuspendedTask> suspendedTasks = new Dictionary<string, SuspendedTask>();
        private ReceiptBook.Entry suspensionReceipt;
        private string suspensionTarget;

        private void HandleTaskSuspension(int client, ControlCommand command, ReceiptBook.Entry receipt)
        {
            if (receipt == null) return;
            if (command.Operation == "forget_task")
            {
                SuspendedTask discarded;
                if (suspendedTasks.TryGetValue(command.ReceiptId, out discarded))
                {
                    discarded.Task.AbandonSuspended("removed_from_queue");
                    discarded.Receipt.State = "cancelled"; discarded.Receipt.Reason = "removed_from_queue";
                    suspendedTasks.Remove(command.ReceiptId);
                }
                receipt.State = "succeeded"; return;
            }
            var original = receipts.Find(command.ReceiptId);
            if (original == null && command.Operation != "suspend") { receipt.State = "rejected"; receipt.Reason = "unknown_task"; return; }
            if (command.Operation == "suspend")
            {
                if (suspensionReceipt != null || (original != null && !original.Terminal && (activeReceipt != original || !tasks.Run.Running)))
                { receipt.State = "rejected"; receipt.Reason = "task_not_suspendable"; return; }
                suspensionReceipt = receipt; suspensionTarget = command.ReceiptId;
                receipt.State = "waiting";
                CompleteSuspension();
                return;
            }
            if (original.Terminal) { receipt.State = "succeeded"; return; }
            SuspendedTask saved;
            if (!suspendedTasks.TryGetValue(original.Id, out saved))
            { receipt.State = "rejected"; receipt.Reason = "task_not_suspended"; return; }
            if (Autonomous || Check(KitchenGate.Inspect()) != null)
            { receipt.State = "rejected"; receipt.Reason = "action_in_progress"; return; }
            // Retain the original task owner across transport reconnects.
            if (!lease.Claim(saved.Task.Run.Owner, Now, true))
            { receipt.State = "rejected"; receipt.Reason = "already_claimed"; return; }
            tasks.Reset("resume_replaced_idle_executor");
            interactions = saved.Child; tasks = saved.Task;
            logistics = new LogisticsActions(navigation, interactions, Buttons, FinishLogistics);
            suspendedTasks.Remove(original.Id);
            Restore(); Install(); navigationSequence = 0;
            activeReceipt = original; receiptOperation = "task"; lastWasTask = true;
            tasks.Resume(); original.State = "running"; original.Reason = null;
            receipt.State = "succeeded";
        }

        private void CompleteSuspension()
        {
            if (suspensionReceipt == null) return;
            CaptureReceipt();
            var original = receipts.Find(suspensionTarget);
            if (original == null) return;
            if (!original.Terminal)
            {
                if (!tasks.Suspend()) return;
                suspendedTasks.Add(original.Id, new SuspendedTask { Task = tasks, Child = interactions, Receipt = original });
                original.State = "suspended"; original.Reason = "urgent_task";
                activeReceipt = null;
                interactions = new InteractionActions(navigation, Buttons, FinishInteraction);
                tasks = new TaskActions(navigation, interactions, Buttons, FinishLogistics);
                logistics = new LogisticsActions(navigation, interactions, Buttons, FinishLogistics);
                lease.Revoke("task_suspended"); Restore();
            }
            dispatchGeneration++;
            suspensionReceipt.State = "succeeded";
            suspensionReceipt = null; suspensionTarget = null;
        }

        private void ClearSuspendedTasks(string reason)
        {
            // Cleanup callbacks belong to the current lease, which is being
            // revoked as part of this same Stop or kitchen invalidation.
            foreach (var saved in suspendedTasks.Values)
            {
                saved.Task.AbandonSuspended(reason);
                saved.Receipt.State = "cancelled"; saved.Receipt.Reason = reason;
            }
            suspendedTasks.Clear();
            if (suspensionReceipt != null)
            { suspensionReceipt.State = "cancelled"; suspensionReceipt.Reason = reason; }
            suspensionReceipt = null; suspensionTarget = null;
        }
    }
}
