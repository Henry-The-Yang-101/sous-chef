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
            public bool Rescue;
        }
        private static AssembledDefinitionNode StageContents(AssemblySchedule.Stage stage)
        { return stage.Processing && stage.Mixing ? RecipeProof.MixingInputs(stage.Vessel) : RecipeProof.Composition(stage.Vessel); }
        private bool StageFits(AssemblySchedule.Stage stage)
        {
            if (stage.Vessel == null || TaskNative.Burnt(stage.Vessel)) return false;
            if (!stage.Processing) return AssemblyBaseValid(stage.Vessel);
            var contents = StageContents(stage);
            return RecipeProof.Subset(contents, stage.Inputs) || RecipeProof.Equal(contents, stage.Goal);
        }
        private bool StageComplete(AssemblySchedule.Stage stage)
        { return stage.Vessel != null && RecipeProof.Equal(RecipeProof.Composition(stage.Vessel), stage.Goal) && TaskNative.Ready(stage.Vessel, stage.Mixing); }
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
        private GameObject SelectStageVessel(AssemblySchedule.Stage stage)
        {
            GameObject best = null; int progress = -1; float distance = Single.MaxValue;
            foreach (var item in assembly.Equipment)
            {
                if (AssemblyDeferred(item) || !VesselFor(stage, item) || TaskNative.Burnt(item)) continue;
                var entry = AssemblyEntry(item); var contents = stage.Mixing ? RecipeProof.MixingInputs(item) : RecipeProof.Composition(item);
                if (entry == null || !Obtainable(entry, "move_item") || !RecipeProof.Subset(contents, stage.Inputs)) continue;
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
        private AssemblyJob SelectAssemblyWork()
        {
            // Rescue owned finished food before doing anything that could delay
            // it. Moving a vessel preserves its exact identity and dependency.
            AssemblyJob rescue = null; float earliest = Single.MaxValue;
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || !StageComplete(stage)) continue;
                var entry = AssemblyEntry(stage.Vessel);
                if (entry == null || !OnProcessor(stage, entry) || !Obtainable(entry, "move_item")) continue;
                float margin = TaskNative.Remaining(stage.Vessel, stage.Mixing, true);
                if (margin < earliest)
                { earliest = margin; rescue = new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Source, Operation = "pick_up", Rescue = true }; }
            }
            if (rescue != null) return rescue;
            if (AssemblyEntry(assembly.Base) == null || !AssemblyBaseValid(assembly.Base)) return null;
            return SelectStageWork(assembly.Root);
        }
        private AssemblyJob SelectStageWork(AssemblySchedule.Stage stage)
        {
            if (stage.Processing && stage.Vessel == null)
            {
                stage.Vessel = SelectStageVessel(stage);
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
                            ApproachCost(target) != Single.MaxValue && CanDeliver(stage.Vessel, target)) return MoveAssembly(stage, entry, target);
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
                            float distance = ApproachCost(target);
                            if (distance < cost) { cost = distance; station = target; }
                        }
                        if (station != null) return MoveAssembly(stage, entry, station);
                    }
                }
                else if (!stage.Processing || !completeInputs)
                {
                    if (entry.Holder == null || !(Counter(entry.Holder) || stage.Processing && stage.Mixing && ProcessingStationFor(stage, entry.Holder)) || TaskNative.Component<ServerConveyorStation>(entry.Holder) != null)
                    {
                        var counter = Parking(stage.Vessel);
                        if (counter != null) return MoveAssembly(stage, entry, counter);
                    }
                    else if (ApproachCost(entry.Holder) != Single.MaxValue)
                    {
                        var source = AssemblySource(stage);
                        if (source != null) return new AssemblyJob { Stage = stage, Source = source.Item, Destination = entry.Holder,
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
                var work = SelectStageWork(childStage); if (work != null) return work;
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
        private LogisticsItem AssemblySource(AssemblySchedule.Stage stage)
        {
            LogisticsItem best = null; int mostParts = -1; bool bestHeld = false; float score = Single.MaxValue;
            var candidates = new List<GameObject>(assembly.Components); candidates.AddRange(assembly.Equipment);
            foreach (var alternative in assembly.Bases)
                if (alternative != null && alternative.GetComponent<PreparationContainer>() != null) candidates.Add(alternative);
            foreach (var source in candidates)
            {
                if (System.Object.ReferenceEquals(source, stage.Vessel) || AssemblyDeferred(source) || !AssemblyTransfer(source, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing)) continue;
                var entry = AssemblyEntry(source);
                if (entry == null || !Obtainable(entry, "move_item")) continue;
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
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && ActionObservations.Occupant(target) == null &&
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
            if (operation == "pick_up") next.UseHeld = cargo => System.Object.ReferenceEquals(cargo, item);
            var abandon = next.AbandonBeforePress;
            next.AbandonBeforePress = () => abandon() || !job.Rescue && AssemblyHeatDue();
            yield return next;
            if (!next.Succeeded)
            {
                assembly.RetryAt[operation == "place_at" ? target : item] = Time.realtimeSinceStartup + 1;
                yield break;
            }
            if (operation == "transfer_contents")
            {
                if (!RecipeProof.Equal(RecipeProof.Combine(before, addition), StageContents(stage)))
                    throw new InvalidOperationException("assembly_composition_changed_after_transfer");
                assembly.Additions++;
                foreach (var owner in assembly.Stages)
                    if (owner != stage && System.Object.ReferenceEquals(owner.Vessel, item)) owner.Vessel = null;
            }
        }
    }
}
