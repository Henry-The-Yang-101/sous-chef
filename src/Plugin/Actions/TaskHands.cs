using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private IEnumerator<TaskStep> handsPlan;
        private TaskStep suspendedStep;

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
            if (step.UseHeld != null && step.UseHeld(held))
            {
                step.Succeeded = true; completedSteps++; step = null; return true;
            }
            if (step.ReplanForHeld != null && step.ReplanForHeld(held))
            {
                step = null; return true;
            }
            suspendedStep = step; step = null;
            handsPlan = (suspendedStep.FreeHands == null ? Park(held) : suspendedStep.FreeHands(held)).GetEnumerator();
            Run.Advance("clearing_hands"); return true;
        }
    }
}
