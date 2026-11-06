using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class AssemblyJob
        {
            public AssemblySchedule.Stage Stage;
            public GameObject Source, Destination;
            public string Operation;
            public bool Rescue, Storage;
        }
        private static AssembledDefinitionNode StageContents(AssemblySchedule.Stage stage)
        { return stage.Processing && stage.Mixing ? RecipeProof.MixingInputs(stage.Vessel) : RecipeProof.Composition(stage.Vessel); }
        private bool StageFits(AssemblySchedule.Stage stage)
        {
            if (stage.Vessel == null || TaskNative.Burnt(stage.Vessel)) return false;
            if (!stage.Processing) return AssemblyBaseValid(stage.Vessel) ||
                stage == assembly.Root && stage.Vessel.GetComponent<PreparationContainer>() != null &&
                RecipeProof.Subset(RecipeProof.Composition(stage.Vessel), stage.Goal);
            var contents = StageContents(stage);
            return RecipeProof.Subset(contents, stage.Inputs) || RecipeProof.Equal(contents, stage.Goal);
        }
        private bool StageComplete(AssemblySchedule.Stage stage)
        { return stage.Vessel != null && RecipeProof.Equal(RecipeProof.Composition(stage.Vessel), stage.Goal) &&
                (!stage.Processing || TaskNative.Ready(stage.Vessel, stage.Mixing)); }
        private bool VesselFor(AssemblySchedule.Stage stage, GameObject item)
        {
            if (item == null || item.GetComponent<ServerIngredientContainer>() == null || TaskNative.ProcessingHandler(item, stage.Mixing) == null) return false;
            var cooked = stage.Goal as CookedCompositeAssembledNode;
            if (cooked != null)
            {
                var handler = item.GetComponent<CookingHandler>();
                var step = handler == null ? null : ActionObservations.Read<CookingStepData>(handler, "m_cookingType");
                if (step == null || cooked.m_cookingStep == null || step.m_uID != cooked.m_cookingStep.m_uID) return false;
            }
            foreach (var other in assembly.Stages)
                if (other != stage && System.Object.ReferenceEquals(other.Vessel, item) &&
                    !(other.Parent == stage && StageComplete(other))) return false;
            return true;
        }
        private GameObject SelectStageVessel(AssemblySchedule.Stage stage, bool probeUnknown)
        {
            GameObject best = null; int progress = -1; float distance = Single.MaxValue;
            var vessels = new List<GameObject>(assembly.Equipment);
            foreach (var item in assembly.Components)
                if (item != null && item.GetComponent<PreparationContainer>() != null) vessels.Add(item);
            foreach (var item in assembly.Bases)
                if (item != null && item.GetComponent<PreparationContainer>() != null) vessels.Add(item);
            foreach (var item in vessels)
            {
                if (AssemblyDeferred(item) || !VesselFor(stage, item) || TaskNative.Burnt(item)) continue;
                var entry = AssemblyEntry(item); var contents = stage.Mixing ? RecipeProof.MixingInputs(item) : RecipeProof.Composition(item);
                if (entry == null || !AssemblyObtainable(entry, "move_item", probeUnknown) || !RecipeProof.Subset(contents, stage.Inputs)) continue;
                int count = RecipeProof.Parts(contents).Count;
                // Do not reserve the only empty bowl for an oven dependency
                // whose mixture still needs that bowl at the mixer.
                if (count == 0 && stage.Children.Count != 0 && !ProcessingInputsAvailable(stage)) continue;
                if (count > progress || count == progress && entry.Distance < distance)
                { best = item; progress = count; distance = entry.Distance; }
            }
            return best;
        }
        private bool ProcessingInputsAvailable(AssemblySchedule.Stage stage)
        {
            var available = AssemblyOutputs(stage.Vessel);
            foreach (var childStage in stage.Children)
            {
                int at = available.FindIndex(value => RecipeProof.Equal(value, childStage.Goal));
                if (at < 0) return false; available.RemoveAt(at);
            }
            return true;
        }
        private List<AssembledDefinitionNode> AssemblyOutputs(GameObject except)
        {
            var available = new List<AssembledDefinitionNode>();
            foreach (var item in assembly.Components)
                if (item != except && AssemblyReady(item)) available.Add(RecipeProof.Composition(item));
            foreach (var item in assembly.Equipment)
                if (item != except && AssemblyReady(item)) available.Add(RecipeProof.Composition(item));
            return available;
        }
        private bool ProcessingStationFor(AssemblySchedule.Stage stage, GameObject station)
        {
            return station != null && assembly.Equipment.Contains(station) && TaskNative.Equipment(station) &&
                (stage.Mixing ? TaskNative.Component<ServerMixingStation>(station) != null || TaskNative.Component<MixingStation>(station) != null :
                    TaskNative.Component<ServerMixingStation>(station) == null && TaskNative.Component<MixingStation>(station) == null);
        }
        private float AssemblyApproachCost(GameObject target, GameObject cargo, bool assignedBatch = false)
        {
            float cost = ApproachCost(target);
            // The broad map may be invalidated immediately after a successful
            // pickup. Preserve useful held cargo, and try the next processor for
            // a bound batch instead of waiting with complete inputs on a counter.
            // The directed child still proves the route before any input.
            return cost == Single.MaxValue && !(bool)inventory.Catalog["complete"] &&
                (assignedBatch || System.Object.ReferenceEquals(ActionObservations.Held(chef), cargo))
                ? 20000 + Vector3.Distance(chef.transform.position, target.transform.position) : cost;
        }
        private AssemblyJob SelectAssemblyWork()
        {
            // Rescue owned finished food before doing anything that could delay
            // it. Moving a vessel preserves its exact identity and dependency.
            AssemblyJob rescue = null; float earliest = Single.MaxValue;
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || !StageComplete(stage)) continue;
                var entry = AssemblyEntry(stage.Vessel);
                // A moving kitchen may keep the broad catalogue frontier
                // incomplete. Let the active, bounded destination planner try
                // the exact hot vessel instead of waiting until it burns.
                if (entry == null || !OnProcessor(stage, entry) || AssemblyDeferred(stage.Vessel) ||
                    !LogisticsChecks.CanCollectEmpty(chef, entry.Source)) continue;
                float margin = TaskNative.Remaining(stage.Vessel, stage.Mixing, true);
                if (margin < earliest)
                { earliest = margin; rescue = new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Source, Operation = "pick_up", Rescue = true }; }
            }
            if (rescue != null) return rescue;
            // An unavailable plate must not block independent recipe work.
            // Keep its selection pending (or its identity pinned) while owned
            // processing dependencies advance and finished food is rescued.
            if (!System.Object.ReferenceEquals(assembly.Base, null) && (assembly.Base == null || !AssemblyBaseValid(assembly.Base))) return null;
            if (assembly.Plating != null && StageComplete(assembly.Root))
                return SelectStageWork(assembly.Plating) ?? SelectStageWork(assembly.Plating, true);
            // Exhaust work with known routes before probing another allocated
            // dependency. Missing entries in an incomplete catalogue are not
            // evidence that every assigned source is unreachable.
            return SelectStageWork(assembly.Root) ?? SelectStageWork(assembly.Root, true) ?? StoreEmptyAssemblyUtensil();
        }
        private AssemblyJob StoreEmptyAssemblyUtensil()
        {
            var held = ActionObservations.Held(chef);
            if (held == null || !(assembly.Equipment.Contains(held) || assembly.Components.Contains(held)) ||
                held.GetComponent<ServerIngredientContainer>() == null || !LogisticsInventory.Empty(held) ||
                held.GetComponent<PreparationContainer>() != null || held.GetComponent<ServerPlate>() != null) return null;
            // Returning an empty assigned utensil while food cooks avoids
            // spending the next ready batch's spoil window freeing our hands.
            var excluded = new HashSet<GameObject>();
            foreach (var pair in assembly.RetryAt) if (AssemblyDeferred(pair.Key)) excluded.Add(pair.Key);
            var counter = Parking(held, excluded: excluded, probeUnknown: true);
            return counter == null ? null : new AssemblyJob { Stage = assembly.Root, Source = held,
                Destination = counter, Operation = "place_at", Storage = true };
        }
        private AssemblyJob SelectStageWork(AssemblySchedule.Stage stage, bool probeUnknown = false)
        {
            if (stage.Processing && stage.Vessel == null)
            {
                stage.Vessel = SelectStageVessel(stage, probeUnknown);
                if (stage.Vessel != null)
                    foreach (var childStage in stage.Children)
                        if (System.Object.ReferenceEquals(childStage.Vessel, stage.Vessel)) childStage.Vessel = null;
            }
            if (stage.Vessel != null && AssemblyEntry(stage.Vessel) != null && StageFits(stage) && !AssemblyDeferred(stage.Vessel))
            {
                var entry = AssemblyEntry(stage.Vessel); var current = StageContents(stage);
                bool completeInputs = RecipeProof.Equal(current, stage.Inputs);
                if (stage.Processing && StageComplete(stage)) return null;
                // Cooking starts with complete inputs. Mixers must be loaded
                // in place: native clients can refuse reinserting a loaded
                // bowl. Each addition proves the mixer's exact input multiset,
                // independently of its transient mixing progress.
                if (stage.Processing && stage.Mixing && !ProcessingStationFor(stage, entry.Holder))
                {
                    foreach (var target in assembly.Equipment)
                        if (!AssemblyDeferred(target) && ProcessingStationFor(stage, target) && ActionObservations.Occupant(target) == null &&
                            AssemblyApproachCost(target, stage.Vessel, true) != Single.MaxValue && CanDeliver(stage.Vessel, target)) return MoveAssembly(stage, entry, target);
                }
                else if (stage.Processing && completeInputs)
                {
                    if (ProcessingStationFor(stage, entry.Holder))
                    {
                        if (ProcessorOff(entry.Holder, stage.Mixing))
                            return new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Holder, Operation = "activate" };
                    }
                    else
                    {
                        GameObject station = null; float cost = Single.MaxValue;
                        foreach (var target in assembly.Equipment)
                        {
                            if (AssemblyDeferred(target) || !ProcessingStationFor(stage, target) || ActionObservations.Occupant(target) != null || !CanDeliver(stage.Vessel, target)) continue;
                            float distance = AssemblyApproachCost(target, stage.Vessel, true);
                            if (distance < cost) { cost = distance; station = target; }
                        }
                        if (station != null) return MoveAssembly(stage, entry, station);
                    }
                }
                else if (!stage.Processing || !completeInputs)
                {
                    if (entry.Holder == null || !(Counter(entry.Holder) || stage.Processing && stage.Mixing && ProcessingStationFor(stage, entry.Holder)) || TaskNative.Component<ServerConveyorStation>(entry.Holder) != null)
                    {
                        var counter = Parking(stage.Vessel, probeUnknown: probeUnknown);
                        if (counter != null) return MoveAssembly(stage, entry, counter);
                    }
                    else
                    {
                        var source = AssemblySource(stage, probeUnknown);
                        if (source != null && AssemblyApproachCost(entry.Holder, source.Item, probeUnknown) != Single.MaxValue)
                            return new AssemblyJob { Stage = stage, Source = source.Item, Destination = entry.Holder,
                            Operation = System.Object.ReferenceEquals(ActionObservations.Held(chef), source.Item) ? "transfer_contents" : "pick_component" };
                    }
                }
            }
            // A blocked ingredient/equipment branch never monopolizes the job.
            // Missing multiplicities determine which processing dependencies
            // remain relevant; completed native outputs are used before more work.
            var missing = RecipeProof.Parts(stage.Inputs);
            if (stage.Vessel != null)
                foreach (var part in RecipeProof.Parts(StageContents(stage)))
                { int at = missing.FindIndex(candidate => AssembledDefinitionNode.Matching(candidate, part)); if (at >= 0) missing.RemoveAt(at); }
            var prepared = AssemblyOutputs(stage.Vessel);
            foreach (var childStage in stage.Children)
            {
                int at = missing.FindIndex(candidate => AssembledDefinitionNode.Matching(candidate, childStage.Goal));
                if (at < 0) continue; missing.RemoveAt(at);
                // A ready assigned output still satisfies demand while held
                // by someone else or unreachable. Do not cook a replacement.
                int ready = prepared.FindIndex(candidate => RecipeProof.Equal(candidate, childStage.Goal));
                if (ready >= 0) { prepared.RemoveAt(ready); continue; }
                var work = SelectStageWork(childStage, probeUnknown); if (work != null) return work;
            }
            return null;
        }
        private static bool ProcessorOff(GameObject station, bool mixing)
        {
            var component = mixing ? (Component)TaskNative.Component<ServerMixingStation>(station) : TaskNative.Component<ServerCookingStation>(station);
            return component != null && station.GetComponent<ClientInteractable>() != null &&
                !ActionObservations.Read<bool>(component, mixing ? "m_bMixerOn" : "m_isTurnedOn");
        }
        private AssemblyJob MoveAssembly(AssemblySchedule.Stage stage, LogisticsItem entry, GameObject destination)
        {
            bool held = System.Object.ReferenceEquals(ActionObservations.Held(chef), stage.Vessel);
            return new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = held ? destination : entry.Source, Operation = held ? "place_at" : "pick_up" };
        }
        private LogisticsItem AssemblySource(AssemblySchedule.Stage stage, bool probeUnknown)
        {
            LogisticsItem best = null; int mostParts = -1; bool bestHeld = false; float score = Single.MaxValue;
            var candidates = new List<GameObject>(assembly.Components); candidates.AddRange(assembly.Equipment);
            foreach (var alternative in assembly.Bases)
                if (alternative != null && alternative.GetComponent<PreparationContainer>() != null) candidates.Add(alternative);
            foreach (var source in candidates)
            {
                if (System.Object.ReferenceEquals(source, stage.Vessel) || AssemblyDeferred(source) || !AssemblyTransfer(source, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing)) continue;
                var entry = AssemblyEntry(source);
                if (entry == null || !AssemblyObtainable(entry, "move_item", probeUnknown)) continue;
                // A staged partial batch belongs to its own dependency, even if
                // a parent could physically accept the same raw components.
                bool reserved = false;
                foreach (var other in assembly.Stages)
                    if (other != stage && System.Object.ReferenceEquals(other.Vessel, source) && !StageComplete(other)) reserved = true;
                if (reserved) continue;
                int parts = RecipeProof.Parts(RecipeProof.Composition(source)).Count;
                bool held = System.Object.ReferenceEquals(ActionObservations.Held(chef), source);
                if (parts > mostParts || parts == mostParts && (held && !bestHeld || held == bestHeld && entry.Distance < score))
                { best = entry; mostParts = parts; bestHeld = held; score = entry.Distance; }
            }
            return best;
        }
        private bool AssemblyPlanningStalled(ref float since)
        {
            if (child.Run.Phase != "recovering_navigation") { since = -1; return false; }
            if (since < 0) since = Time.realtimeSinceStartup;
            return Time.realtimeSinceStartup - since >= 2;
        }
        private bool AssemblyHeatDue()
        {
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || stage.Vessel == null) continue;
                var entry = AssemblyEntry(stage.Vessel);
                if (entry != null && OnProcessor(stage, entry) && StageComplete(stage)) return true;
            }
            return false;
        }
        private static bool OnProcessor(AssemblySchedule.Stage stage, LogisticsItem entry)
        {
            // Inventory is a planning snapshot. Do not rescue a vessel twice
            // after pickup because its previous holder is still cached.
            return TaskNative.Equipment(entry.Holder) &&
                System.Object.ReferenceEquals(ActionObservations.Occupant(entry.Holder), stage.Vessel);
        }
        private IEnumerable<TaskStep> ExecuteAssembly(AssemblyJob job)
        {
            var stage = job.Stage; var item = job.Source; var target = job.Destination;
            assembly.Record(job.Rescue ? "rescue_finished_batch" : job.Storage ? "park_empty_utensil" : job.Operation, stage, item, target);
            Func<bool> valid = () => StageFits(stage);
            string operation = job.Operation;
            if (operation == "pick_component")
            {
                var entry = AssemblyEntry(item); if (entry == null) yield break;
                target = ActionObservations.PickupTarget(entry.Source, item); operation = "pick_up";
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Occupant(job.Destination), stage.Vessel) &&
                    AssemblyTransfer(item, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing);
            }
            else if (operation == "place_at")
                valid = () => (job.Storage ? LogisticsInventory.Empty(item) : StageFits(stage)) && System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && ActionObservations.Occupant(target) == null &&
                    (!TaskNative.Equipment(target) || ProcessingStationFor(stage, target) && (stage.Mixing ? RecipeProof.Subset(StageContents(stage), stage.Inputs) : RecipeProof.Equal(RecipeProof.Composition(item), stage.Inputs)));
            else if (operation == "activate")
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Occupant(target), item) &&
                    RecipeProof.Equal(RecipeProof.Composition(item), stage.Inputs) && ProcessorOff(target, stage.Mixing);
            var before = StageContents(stage); var addition = RecipeProof.Composition(item);
            if (operation == "transfer_contents")
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && System.Object.ReferenceEquals(ActionObservations.Occupant(target), stage.Vessel) &&
                    RecipeProof.Equal(before, StageContents(stage)) && RecipeProof.Equal(addition, RecipeProof.Composition(item)) &&
                    AssemblyTransfer(item, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing);
            var next = AssemblyStep(operation, target, valid, operation == "transfer_contents" && stage.Processing && stage.Mixing ? "assembly_mixing_inputs" : null);
            var beforePress = next.BeforePress;
            next.BeforePress = () => {
                bool allowed = beforePress();
                assembly.Record(allowed ? "press_authorized" : "press_rejected", stage, item, target);
                return allowed;
            };
            if (operation == "pick_up") next.UseHeld = cargo => System.Object.ReferenceEquals(cargo, item);
            var abandon = next.AbandonBeforePress;
            float planningSince = -1;
            next.AbandonBeforePress = () => abandon() || !job.Rescue &&
                (!job.Storage && AssemblyHeatDue() || AssemblyPlanningStalled(ref planningSince));
            yield return next;
            if (!next.Succeeded)
            {
                assembly.Record("replan_uncompleted_step", stage, item, target);
                assembly.RetryAt[operation == "place_at" ? target : item] = Time.realtimeSinceStartup + 1;
                yield break;
            }
            if (operation == "transfer_contents")
            {
                if (!RecipeProof.Equal(RecipeProof.Combine(before, addition), StageContents(stage)))
                    throw new InvalidOperationException("assembly_composition_changed_after_transfer");
                assembly.Additions++;
                assembly.Record("transfer_verified", stage, item, target);
                foreach (var owner in assembly.Stages)
                    if (owner != stage && System.Object.ReferenceEquals(owner.Vessel, item)) owner.Vessel = null;
            }
        }
    }
}
