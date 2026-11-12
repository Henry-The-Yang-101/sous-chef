using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class AssemblyNeed
        {
            public AssemblySchedule.Stage Stage;
            public AssembledDefinitionNode Goal;
        }

        private void RefreshAssemblyResources()
        {
            if (!assembly.Automatic) return;
            // Keep acquired identities and stage ownership. Newly observed stock
            // (including conveyor arrivals) becomes eligible without restarting.
            foreach (var entry in inventory.Items)
            {
                if (entry.Item == null) continue;
                if (entry.Supply)
                {
                    if (UsefulAssemblyIngredient(entry.Item) && !assembly.Supplies.Contains(entry.Source)) assembly.Supplies.Add(entry.Source);
                    continue;
                }
                var item = LogisticsChecks.Payload(entry);
                if (item == null) continue;
                if (AssemblyResources.PlateFits(assembly.Order, item))
                { if (!assembly.Bases.Contains(item)) assembly.Bases.Add(item); }
                else if (assembly.Stages.Exists(stage => stage.Processing && AssemblyResources.VesselFor(stage, item)))
                { if (!assembly.Equipment.Contains(item) && !assembly.Components.Contains(item)) assembly.Equipment.Add(item); }
                else if (UsefulAssemblyIngredient(item) && !assembly.Components.Contains(item)) assembly.Components.Add(item);
            }
            foreach (var target in navigation.Targets())
                if (TaskNative.Equipment(target) && !assembly.Equipment.Contains(target)) assembly.Equipment.Add(target);
        }

        private bool UsefulAssemblyIngredient(GameObject item)
        {
            if (TaskNative.Burnt(item)) return false;
            var contents = AssemblyPreparation.Composition(item);
            return contents != null && !RecipeProof.Empty(contents) &&
                assembly.Stages.Exists(stage => RecipeProof.Subset(contents, stage.Inputs));
        }

        private List<AssemblyNeed> AssemblyNeeds()
        {
            var ready = new List<AssembledDefinitionNode>();
            var seen = new HashSet<GameObject>();
            foreach (var item in assembly.Components.Concat(assembly.Equipment).Concat(
                assembly.Bases.Where(item => item != null && item.GetComponent<PreparationContainer>() != null)))
                if (item != null && seen.Add(item) && !assembly.Stages.Exists(stage => stage.Vessel == item) && AssemblyResources.Ready(item))
                {
                    var composition = RecipeProof.Composition(item);
                    if (assembly.Stages.Exists(stage => RecipeProof.Subset(composition, stage.Inputs))) ready.AddRange(RecipeProof.Parts(composition));
                }
            var needs = new List<AssemblyNeed>();
            CollectAssemblyNeeds(assembly.Root, ready, needs);
            return needs;
        }

        private void CollectAssemblyNeeds(AssemblySchedule.Stage stage, List<AssembledDefinitionNode> ready, List<AssemblyNeed> needs)
        {
            if (StageComplete(stage)) return;
            var missing = RecipeProof.Parts(stage.Inputs);
            if (stage.Vessel != null && StageFits(stage))
                foreach (var part in RecipeProof.Parts(StageContents(stage)))
                { int index = missing.FindIndex(goal => RecipeProof.Equal(part, goal)); if (index >= 0) missing.RemoveAt(index); }
            var children = new List<AssemblySchedule.Stage>(stage.Children);
            foreach (var part in missing)
            {
                int index = ready.FindIndex(value => RecipeProof.Equal(value, part));
                if (index >= 0) { ready.RemoveAt(index); continue; }
                var childStage = children.Find(child => RecipeProof.Equal(child.Goal, part));
                if (childStage != null) { children.Remove(childStage); CollectAssemblyNeeds(childStage, ready, needs); }
                else if (part is IngredientAssembledNode) needs.Add(new AssemblyNeed { Stage = stage, Goal = part });
            }
        }

        private AssemblyJob SelectAssemblyPreparation(bool probeUnknown)
        {
            var reserved = new HashSet<GameObject>();
            bool blockedBoard = false;
            foreach (var need in AssemblyNeeds())
            {
                LogisticsItem best = null; float score = Single.MaxValue; GameObject existing = null;
                foreach (var item in assembly.Components)
                {
                    if (item == null || reserved.Contains(item) || item.GetComponent<WorkableItem>() == null || !AssemblyPreparation.Produces(item, need.Goal)) continue;
                    // Existing raw portions count even while another chef holds
                    // them. A dispenser must not duplicate that demand.
                    existing = item;
                    var entry = AssemblyEntry(item);
                    if (entry == null || AssemblyDeferred(item) || !AssemblyObtainable(entry, "move_item", probeUnknown)) continue;
                    float cost = entry.Distance - (OnBoard(entry) ? 100000 : 0) - (ActionObservations.Held(chef) == item ? 10000 : 0);
                    if (cost < score) { best = entry; score = cost; }
                }
                if (best == null && existing == null)
                    foreach (var entry in inventory.Items)
                    {
                        if (!entry.Supply || !assembly.Supplies.Contains(entry.Source) || AssemblyDeferred(entry.Source) ||
                            !AssemblyPreparation.Produces(entry.Item, need.Goal) || !AssemblyObtainable(entry, "move_item", probeUnknown)) continue;
                        float cost = AssemblyApproachCost(entry.Source, null, probeUnknown);
                        if (cost < score) { best = entry; score = cost; }
                    }
                if (best == null) { if (existing != null) reserved.Add(existing); continue; }
                if (!best.Supply) reserved.Add(best.Item);
                GameObject board = null;
                if (best.Item.GetComponent<WorkableItem>() != null)
                {
                    bool occupied = false;
                    board = OnBoard(best) ? best.Holder : Board(best.Item, out occupied, best.Supply);
                    if (board == null) { blockedBoard |= occupied; assembly.Waiting = "waiting_for_chopping_board"; continue; }
                }
                string operation = best.Supply ? "dispense_component" : OnBoard(best) ? "prepare_component" :
                    ActionObservations.Held(chef) == best.Item ? "stage_component" : "fetch_raw_component";
                return new AssemblyJob { Stage = need.Stage, Source = best.Supply ? best.Source : best.Item,
                    Destination = operation == "prepare_component" || operation == "stage_component" ? board : best.Source,
                    Operation = operation, Preparation = need.Goal };
            }
            if (blockedBoard)
                foreach (var entry in inventory.Items)
                    if (OnBoard(entry))
                    {
                        var clear = AssemblyClearSurface(entry.Holder);
                        if (clear != null) return clear;
                    }
            return null;
        }

        private IEnumerable<TaskStep> ExecuteAssemblyPreparation(AssemblyJob job)
        {
            var source = job.Source; var target = job.Destination;
            bool supply = job.Operation == "dispense_component", work = job.Operation == "prepare_component";
            string operation = work ? "work_at" : job.Operation == "stage_component" ? "place_at" : "pick_up";
            var prefab = supply ? source.GetComponent<PickupItemSpawner>().m_itemPrefab : null;
            Func<bool> needed = () => AssemblyNeeds().Exists(need => need.Stage == job.Stage && RecipeProof.Equal(need.Goal, job.Preparation));
            Func<bool> valid = () => needed() && (supply ? source != null && source.GetComponent<PickupItemSpawner>() != null &&
                source.GetComponent<PickupItemSpawner>().m_itemPrefab == prefab && AssemblyPreparation.Produces(prefab, job.Preparation) :
                source != null && AssemblyPreparation.Produces(source, job.Preparation) &&
                (!work || ActionObservations.Occupant(target) == source));
            var next = AssemblyStep(operation, target, valid, work ? "work_complete" : null);
            next.FreeHands = ClearAssemblyHands;
            if (operation == "pick_up") next.UseHeld = held => !supply && held == source;
            var authorize = next.BeforePress;
            next.BeforePress = () => authorize() && !AssemblyHeatDue(true) && AssemblyTripDeadline(job) == null;
            var abandon = next.AbandonBeforePress; float planningSince = -1; bool stalled = false; Vector3 planningPosition = Vector3.zero; Collider planningSupport = null;
            next.AbandonBeforePress = () => abandon() || AssemblyHeatDue(true) || AssemblyTripDeadline(job) != null || (stalled = AssemblyPlanningStalled(ref planningSince, ref planningPosition, ref planningSupport));
            assembly.Record(job.Operation, job.Stage, source, target);
            yield return next;
            if (!next.Succeeded)
            {
                if (next.RouteFailed || stalled) DeferAssemblyRoute(source, target);
                else assembly.RetryAt[source] = Time.realtimeSinceStartup + .2f;
                yield break;
            }
            if (supply || work)
            {
                var output = work ? ActionObservations.Occupant(target) : ActionObservations.Held(chef);
                if (output == null || !AssemblyPreparation.Produces(output, job.Preparation))
                    throw new InvalidOperationException("assembly_preparation_output_changed");
                if (work) assembly.Components.Remove(source);
                if (!assembly.Components.Contains(output)) assembly.Components.Add(output);
                assembly.Record(work ? "preparation_verified" : "ingredient_acquired", job.Stage, output, target);
            }
        }
    }
}
