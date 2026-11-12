using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private float preemptedAt;
        private GameObject suspendedCargo;
        private IEnumerator<TaskStep> restoration;
        private TaskStep restorationStep;
        private InteractionActions restoringChild;
        private bool restoringStarted;
        private sealed class RestorationState
        {
            public IEnumerator<TaskStep> Plan;
            public TaskStep Step;
            public InteractionActions Child;
            public bool Started;
            public GameObject Cargo;
            public float PreemptedAt;
        }
        private readonly Stack<RestorationState> restorationStack = new Stack<RestorationState>();

        public void AbandonSuspended(string reason)
        {
            child.AbandonSuspended(reason);
            ClearRestoration(reason);
            if (plan != null) plan.Dispose(); plan = null;
            if (handsPlan != null) handsPlan.Dispose(); handsPlan = null;
            foreach (var observer in observers.Values) observer.Dispose(); observers.Clear();
            Run.Finish("cancelled", reason);
        }
        public void TickChild()
        {
            if (restoration == null) child.Tick();
        }

        public bool Suspend()
        {
            if (navigation.NativeTeleportInProgress(chef)) return false;
            if (!(restoration == null ? child : restoringChild).SuspendTask()) return false;
            if (restoration != null)
                restorationStack.Push(new RestorationState { Plan = restoration, Step = restorationStep,
                    Child = restoringChild, Started = restoringStarted, Cargo = suspendedCargo, PreemptedAt = preemptedAt });
            navigation.Stop("task_suspended");
            suspendedCargo = ActionObservations.Held(chef);
            preemptedAt = Time.realtimeSinceStartup;
            return true;
        }

        private void ClearRestoration(string reason)
        {
            if (restoration != null) restoration.Dispose();
            if (restoringChild != null) restoringChild.AbandonSuspended(reason);
            restoration = null; restoringChild = null;
            foreach (var saved in restorationStack)
            { saved.Plan.Dispose(); saved.Child.AbandonSuspended(reason); }
            restorationStack.Clear();
        }

        public void Resume()
        {
            restoringChild = new InteractionActions(navigation, input, () => input(0));
            restoration = RecoverCargo().GetEnumerator();
            restorationStep = null;
            restoringStarted = false;
        }

        private IEnumerable<TaskStep> RecoverCargo()
        {
            GameObject held = ActionObservations.Held(chef);
            if (!System.Object.ReferenceEquals(suspendedCargo, null) && suspendedCargo == null)
                throw new InvalidOperationException("suspended_cargo_unavailable");
            if (held == suspendedCargo) yield break;
            if (held != null)
                foreach (TaskStep parking in Park(held)) yield return parking;
            if (suspendedCargo == null) yield break;
            LogisticsItem found = null;
            yield return Until("waiting_for_suspended_cargo", delegate {
                if (suspendedCargo == null || !suspendedCargo.activeInHierarchy)
                    throw new InvalidOperationException("suspended_cargo_unavailable");
                RefreshWorld();
                foreach (LogisticsItem item in inventory.Items)
                    if (System.Object.ReferenceEquals(item.Item, suspendedCargo) && Obtainable(item, "move_item"))
                    { found = item; return true; }
                return false;
            });
            yield return Do("pick_up", found.Source);
        }

        private void RestoreCargo()
        {
            inventory.BeginObservation();
            try
            {
                if (restoringStarted)
                {
                    restoringChild.Tick();
                    if (restoringChild.Run.Running) return;
                    if (restoringChild.Run.State != "succeeded")
                    { Finish("blocked", "suspended_cargo_recovery_failed"); return; }
                    restorationStep.Succeeded = true;
                    restorationStep = null;
                    restoringStarted = false;
                }
                if (restorationStep == null)
                {
                    if (!restoration.MoveNext())
                    {
                        restoration.Dispose(); restoration = null; restoringChild = null;
                        float elapsed = Time.realtimeSinceStartup - preemptedAt;
                        if (restorationStack.Count > 0)
                        {
                            var saved = restorationStack.Pop();
                            restoration = saved.Plan; restorationStep = saved.Step; restoringChild = saved.Child;
                            restoringStarted = saved.Started; suspendedCargo = saved.Cargo; preemptedAt = saved.PreemptedAt;
                            restoringChild.ResumeTask(elapsed);
                        }
                        else { ResumeAfterPause(elapsed); child.ResumeTask(elapsed); }
                        return;
                    }
                    restorationStep = restoration.Current;
                }
                waitingReason = restorationStep.Phase;
                if (restorationStep.Ready != null && !restorationStep.Ready()) return;
                if (restorationStep.Command == null) { restorationStep = null; return; }
                var command = restorationStep.Command();
                if (command == null) return;
                command.Id = Run.RequestId; command.Indefinite = true;
                string error = restoringChild.Start(chef, Run.Owner, command, restorationStep.BeforePress);
                if (error != null) { waitingReason = error; return; }
                restoringStarted = true;
            }
            catch (Exception error) { observationError = error.ToString(); Finish("blocked", "suspended_cargo_unavailable"); }
            finally { inventory.EndObservation(); }
        }
    }
}
