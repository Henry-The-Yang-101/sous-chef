using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private IEnumerator<TaskStep> handsPlan;
        private TaskStep suspendedStep;
        private GameObject handsCargo;

        private void ObserveHandsRecovery()
        {
            if (handsPlan == null || System.Object.ReferenceEquals(ActionObservations.Held(chef), handsCargo)) return;
            // A catch while use is held can leave the hands on the following
            // native input release. Once it has left, there is nothing to park.
            // Let an issued placement finish verification before resuming; never
            // repeat its edge or abandon the original task's claimed food.
            if (childStarted && child.Run.Running && !child.CanRetry) return;
            if (childStarted)
            {
                if (child.Run.State == "succeeded") completedSteps++;
                child.Cancel("task_hands_changed"); childStarted = false;
            }
            handsPlan.Dispose(); handsPlan = null; handsCargo = null;
            step = suspendedStep; suspendedStep = null; retryAt = 0; waitingReason = null;
        }

        // A catch can happen before dispatch, during walking, or while working.
        // Suspend only an unpressed action (or resumable native work), retaining
        // its iterator, source checks and the parent's single request/lease.
        private bool ReconcileHands()
        {
            if (step == null || !step.EmptyHands) return false;
            GameObject held = ActionObservations.Held(chef);
            if (held == null) return false;
            if (childStarted)
            {
                if (child.Run.State == "succeeded") return false;
                if (!child.CanRetry && !(step.Phase == "work_at" && child.Run.State == "blocked" && child.Run.Reason == "hands_full")) return false;
                child.Cancel("task_reconcile_hands"); childStarted = false;
            }
            input(0); waitingReason = null; retryAt = 0;
            if (!Foreign(held) && step.UseHeld != null && step.UseHeld(held))
            {
                step.Succeeded = true; ObserveWorkOwnership(step); completedSteps++; step = null; return true;
            }
            if (!Foreign(held) && step.ReplanForHeld != null && step.ReplanForHeld(held))
            {
                step = null; return true;
            }
            suspendedStep = step; step = null;
            handsCargo = held;
            handsPlan = (suspendedStep.FreeHands == null ? Park(held) : suspendedStep.FreeHands(held)).GetEnumerator();
            Run.Advance("clearing_hands"); return true;
        }
    }
}
