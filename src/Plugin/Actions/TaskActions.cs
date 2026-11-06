using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class TaskStep
    {
        public string Phase;
        public Func<ControlCommand> Command;
        public Func<bool> Ready;
        public Func<bool> Progress;
        public Func<bool> Outcome, AbandonBeforePress;
        public Func<GameObject, bool> UseHeld, ReplanForHeld;
        public Func<GameObject, IEnumerable<TaskStep>> FreeHands;
        public bool EmptyHands;
        public bool ReplanOnBlock, ReplanAfterPress, Succeeded;
        public GameObject Obstruction;
    }
    internal sealed class TaskDefinition
    {
        public string Name;
        public Func<bool> Available;
        public Func<IEnumerable<TaskStep>> Plan;
    }
    // One durable request and lease. Iterators advance only after observed child
    // success, so waiting/repeating cannot replay an already completed pickup.
    internal sealed partial class TaskActions
    {
        public readonly ActionRun Run = new ActionRun();
        private readonly NavigationActions navigation;
        private readonly InteractionActions child;
        private readonly LogisticsInventory inventory;
        private readonly Action<int> input;
        private readonly Action release;
        private readonly Dictionary<string, TaskDefinition> definitions = new Dictionary<string, TaskDefinition>();
        private PlayerControls chef;
        private TaskRequest request;
        private IEnumerator<TaskStep> plan;
        private TaskStep step;
        private bool childStarted;
        private long completed, completedSteps;
        private string waitingReason;
        private string observationError;
        private float retryAt;
        public TaskActions(NavigationActions nav, InteractionActions primitive, Action<int> buttons, Action finished)
        {
            navigation = nav; child = primitive; inventory = new LogisticsInventory(nav); input = buttons; release = finished;
            groundPass = new GroundPassAccess(CanReachLanding, CanCollectLanding);
            handoffThrows = new ThrowOptions(nav, groundPass); child.GroundAccess = groundPass;
            RegisterPreparation(); RegisterTakeReady(); RegisterWashing(); RegisterDirtyDelivery(); RegisterFire(); RegisterBurnt(); RegisterMonitor(); RegisterDishDelivery();
        }
        partial void RegisterPreparation(); partial void RegisterTakeReady(); partial void RegisterWashing();
        partial void RegisterDirtyDelivery(); partial void RegisterFire(); partial void RegisterBurnt(); partial void RegisterMonitor();
        partial void RegisterDishDelivery();
        private void Register(string name, Func<bool> available, Func<IEnumerable<TaskStep>> steps)
        { definitions.Add(name, new TaskDefinition { Name = name, Available = available, Plan = steps }); }
        public object Describe()
        {
            return Run.Describe(Json.Object("taskAction", request == null ? null : request.Action,
                "repetitions", request == null ? 1 : request.Repetitions, "infinite", request != null && request.Repetitions == 0,
                "completedRepetitions", completed, "completedSteps", completedSteps, "waitingReason", waitingReason,
                "preparation", preparation == null ? null : preparation.Describe(), "handoff", handoffEvidence, "pendingDeliveries", pendingDeliveries.Count, "observationError", observationError,
                "handoffPlanning", waitingReason == "waiting_for_player_handoff" ? handoffThrows.Describe() : null,
                "child", childStarted ? child.Describe() : null));
        }
        public string Start(PlayerControls actor, int owner, ControlCommand command)
        {
            if (Run.Running || child.Run.Running || navigation.Action.State != "idle") return "action_in_progress";
            TaskDefinition definition;
            if (command.Task == null || !definitions.TryGetValue(command.Task.Action, out definition)) return "unsupported_task";
            chef = actor; request = command.Task; inventory.Refresh(chef);
            if (!definition.Available()) return "task_not_available";
            pendingDeliveries.Clear(); nextDeliveryCheck = 0;
            preparation = null; completed = completedSteps = 0; childStarted = false; waitingReason = null; observationError = null; handoffEvidence = null; step = null; plan = definition.Plan().GetEnumerator(); retryAt = 0;
            command.Indefinite = true; Run.Start(owner, command, Time.realtimeSinceStartup); input(0); return null;
        }
        public void Tick()
        {
            if (!Run.Running) return;
            inventory.BeginObservation();
            try
            {
                ObserveDeliveries();
                ObserveHandsRecovery();
                if (!child.Run.Running && navigation.Action.State == "idle") handoffThrows.Tick(chef);
                if (childStarted)
                {
                    if (ReconcileHands()) return;
                    if (child.Run.Running && step.ReplanOnBlock && child.CanRetry &&
                        (child.NavigationStalled || (step.AbandonBeforePress != null && step.AbandonBeforePress())))
                    {
                        // No native button has been pressed: storage/handoff may
                        // choose another checked destination without replaying cargo.
                        step.Obstruction = child.Obstruction;
                        child.Cancel("task_replan_before_press"); childStarted = false;
                        step = null; Wait("waiting_for_alternative_destination"); return;
                    }
                    if (child.Run.Running) return;
                    childStarted = false;
                    if (child.Run.State != "succeeded" && (step.Outcome == null || !step.Outcome()))
                    {
                        // The exact pickup cargo no longer exists. Choosing a
                        // fresh source cannot replay a transfer of that item.
                        if (step.ReplanOnBlock && (child.CanRetry || child.Run.Reason == "pickup_item_destroyed"))
                        { step = null; Wait(child.Run.Reason); return; }
                        // Pressed or released: the plan reconciles the cargo's
                        // observed fate itself and never replays the press.
                        if (step.ReplanAfterPress && child.Run.State != "cancelled") { step = null; Wait(child.Run.Reason); return; }
                        if (child.Run.State == "blocked" && child.CanRetry) { Wait(child.Run.Reason); return; }
                        if (!ActionRecovery.Transient(child.Run.Reason)) { Finish(child.Run.State == "cancelled" ? "cancelled" : "failed", child.Run.Reason); return; }
                        Wait(child.Run.Reason); return;
                    }
                    step.Succeeded = true;
                    completedSteps++; step = null;
                }
                if (step != null && step.Progress != null)
                { if (step.Progress()) { input(0); completedSteps++; step = null; } return; }
                input(0);
                if (Time.realtimeSinceStartup < retryAt) return;
                if (plan == null)
                {
                    if (pendingDeliveries.Count == 0) Finish("succeeded", "task_complete");
                    else Wait("waiting_for_transport_delivery");
                    return;
                }
                if (step == null)
                {
                    if (handsPlan != null)
                    {
                        if (handsPlan.MoveNext()) step = handsPlan.Current;
                        else { handsPlan.Dispose(); handsPlan = null; handsCargo = null; step = suspendedStep; suspendedStep = null; }
                    }
                }
                if (step == null)
                {
                    if (!plan.MoveNext())
                    {
                        plan.Dispose(); plan = null;
                        if (pendingDeliveries.Count == 0) Finish("succeeded", "task_complete");
                        else { Run.Advance("waiting_for_transport_delivery"); Wait("waiting_for_transport_delivery"); }
                        return;
                    }
                    step = plan.Current;
                }
                if (ReconcileHands()) return;
                Run.Advance(step.Phase); waitingReason = null;
                if (step.Progress != null) return;
                if (step.Ready != null && !step.Ready())
                {
                    // Like a failed child precondition, no button has been pressed.
                    string phase = step.Phase; if (step.ReplanOnBlock) step = null;
                    Wait(phase); return;
                }
                if (step.Command == null) { completedSteps++; step = null; return; }
                ControlCommand command = step.Command();
                if (command == null) { Wait(step.Phase); return; }
                command.Id = Run.RequestId; command.Indefinite = true;
                string error = child.Start(chef, Run.Owner, command);
                if (error != null)
                {
                    if (step.ReplanOnBlock) step = null;
                    // A failed precondition has not pressed a button. Revalidate
                    // the same step when the environment allows it again.
                    Wait(error); return;
                }
                childStarted = true;
            }
            catch (Exception e) { observationError = e.ToString(); Finish("failed", "task_observation_unavailable"); }
            finally { inventory.EndObservation(); }
        }
        private bool MoreRequested { get { return request.Repetitions == 0 || completed < request.Repetitions; } }
        private void Wait(string reason) { waitingReason = reason; input(0); retryAt = Time.realtimeSinceStartup + .2f; }
        public void Cancel(string reason) { if (Run.Running) Finish("cancelled", reason); }
        public void ResumeAfterPause(float seconds)
        {
            Run.Delay(seconds); retryAt += seconds; nextDeliveryCheck += seconds;
            foreach (CounterMotion motion in counterMotion.Values) motion.Delay(seconds);
        }
        public void Reset(string reason)
        {
            Cancel(reason); inventory.Reset(); counterMotion.Clear(); humanFrame = -1; humanDistances.Clear(); deliveredItems.Clear(); pendingDeliveries.Clear();
            foreach (NavigationActions observer in observers.Values) observer.Dispose(); observers.Clear();
        }
        private void Finish(string state, string reason)
        {
            // Keep the parent running during child cleanup so its callback cannot
            // release the lease before all owned input has been neutralized.
            child.Cancel(reason); input(0); Run.Finish(state, reason);
            handoffThrows.Reset();
            if (navigation.Action.State == "navigating") navigation.Stop(reason);
            if (plan != null) plan.Dispose(); plan = null; step = null;
            if (handsPlan != null) handsPlan.Dispose(); handsPlan = null; handsCargo = null; suspendedStep = null;
            release();
        }
        private TaskStep Do(string operation, GameObject target, string condition = null)
        {
            if (operation == "pick_up") target = ActionObservations.PickupTarget(target);
            GameObject originalHeld = ActionObservations.Held(chef), originalOccupant = ActionObservations.Occupant(target);
            var supply = target == null ? null : target.GetComponent<PickupItemSpawner>();
            GameObject originalPrefab = supply == null ? null : supply.m_itemPrefab;
            GameObject expected = originalOccupant ?? target;
            var stack = expected == null ? null : expected.GetComponent<ServerStack>();
            if (expected != null && expected.GetComponent<CleanPlateStack>() != null) expected = stack == null ? null : stack.InspectTopOfStack();
            return new TaskStep { Phase = operation,
                EmptyHands = operation == "pick_up" || operation == "work_at" || operation == "activate",
                UseHeld = operation == "pick_up" ? (Func<GameObject, bool>)(held => System.Object.ReferenceEquals(held, expected)) : null,
                Ready = delegate {
                if (target == null || !target.activeInHierarchy) return false;
                if (operation == "pick_up")
                    return System.Object.ReferenceEquals(ActionObservations.Occupant(target), originalOccupant) &&
                        (supply == null || System.Object.ReferenceEquals(supply.m_itemPrefab, originalPrefab));
                if ((operation == "place_at" || operation == "add_ingredient" || operation == "collect_contents" || operation == "discard") &&
                    !System.Object.ReferenceEquals(ActionObservations.Held(chef), originalHeld)) throw new InvalidOperationException("task_cargo_changed");
                return operation != "place_at" && operation != "collect_contents" && operation != "work_at" || System.Object.ReferenceEquals(ActionObservations.Occupant(target), originalOccupant);
            }, Command = delegate { return new ControlCommand {
                Operation = operation, Destination = navigation.RegisterItem(target), Condition = condition, DesiredState = "trigger" }; } };
        }
        private static TaskStep Until(string phase, Func<bool> ready) { return new TaskStep { Phase = phase, Ready = ready }; }
        public void Decorate(Json.Record catalog, PlayerControls actor, bool parentBusy = false)
        {
            inventory.BeginObservation();
            try { DecorateSnapshot(catalog, actor, parentBusy); }
            finally { inventory.EndObservation(); }
        }
        private void DecorateSnapshot(Json.Record catalog, PlayerControls actor, bool parentBusy)
        {
            var choices = new List<object>(); catalog["taskOptions"] = choices;
            catalog["task"] = Describe();
            if (parentBusy || Run.Running || child.Run.Running || navigation.Action.State != "idle") return;
            chef = actor; inventory.Refresh(chef, catalog);
            var preparation = new List<object>(); var selectors = new HashSet<string>();
            // Dispensers and automatic spawns define the ingredient trees.
            // Loose processed food must not change the set of selectable roots.
            foreach (PreparationIngredient root in inventory.IngredientRoots)
                preparation.Add(Json.Object("id", root.Id, "label", root.Name, "utensil", false));
            foreach (LogisticsItem item in inventory.Items)
            {
                string type = item.CleanPlate ? "plate" : item.UtensilType;
                if (type != null && selectors.Add("utensil:" + type))
                    preparation.Add(Json.Object("id", type, "label", type, "utensil", true));
            }
            catalog["preparationOptions"] = preparation;
            foreach (var pair in definitions)
            {
                try { if (pair.Value.Available()) choices.Add(Json.Object("name", pair.Key)); }
                catch (Exception) { /* Incomplete observations never authorize work. */ }
            }
        }
    }
}
