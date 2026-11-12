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
            public string Utility;
            public AssembledDefinitionNode Preparation;
            public bool Rescue, Storage;
        }
        private static AssembledDefinitionNode StageContents(AssemblySchedule.Stage stage)
        { return AssemblyResources.ContentsFor(stage, stage.Vessel); }
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
            if (!AssemblyResources.VesselFor(stage, item)) return false;
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
                if (item != except && AssemblyResources.Ready(item)) available.Add(RecipeProof.Composition(item));
            foreach (var item in assembly.Equipment)
                if (item != except && AssemblyResources.Ready(item)) available.Add(RecipeProof.Composition(item));
            return available;
        }
        private bool ProcessingStationFor(AssemblySchedule.Stage stage, GameObject station)
        { return assembly.Equipment.Contains(station) && AssemblyResources.StationFor(stage, station); }
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
            assembly.Waiting = null;
            // Rescue owned finished food before doing anything that could delay
            // it. Moving a vessel preserves its exact identity and dependency.
            AssemblyJob rescue = null, approaching = null; float earliest = Single.MaxValue, soonest = Single.MaxValue;
            bool readyPending = false;
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || stage.Vessel == null || !StageFits(stage)) continue;
                var entry = AssemblyEntry(stage.Vessel);
                if (entry == null || !OnProcessor(stage, entry)) continue;
                if (!StageComplete(stage))
                {
                    if (stage.Mixing || !RecipeProof.Equal(StageContents(stage), stage.Inputs) || ProcessorOff(entry.Holder, false)) continue;
                    float remaining = TaskNative.Remaining(stage.Vessel);
                    // Leave time to put down current cargo and approach the
                    // batch before it finishes, rather than spending its spoil
                    // window recovering storage after a distant plating trip.
                    float returnSeconds = AssemblyReturnSeconds(entry.Source);
                    if (remaining <= returnSeconds && remaining < soonest)
                    { soonest = remaining; approaching = new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Source, Operation = "approach_heat" }; }
                    continue;
                }
                readyPending = true;
                // A moving kitchen may keep the broad catalogue frontier
                // incomplete. Let the active, bounded destination planner try
                // the exact hot vessel instead of waiting until it burns.
                if (AssemblyDeferred(stage.Vessel) ||
                    !LogisticsChecks.CanCollectEmpty(chef, entry.Source)) continue;
                float margin = TaskNative.Remaining(stage.Vessel, stage.Mixing, true);
                if (margin < earliest)
                { earliest = margin; rescue = new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Source, Operation = "pick_up", Rescue = true }; }
            }
            if (rescue != null) return rescue;
            // A brief rescue retry delay does not authorize a cold-food pickup.
            if (readyPending) { assembly.Waiting = "waiting_for_heat_access"; return null; }
            if (approaching != null)
            {
                var held = ActionObservations.Held(chef);
                if (held == null) return approaching;
                assembly.Waiting = "waiting_for_heat_storage";
                return StoreAssemblyCargo(held);
            }
            // An unavailable plate must not block independent recipe work.
            // Keep its selection pending (or its identity pinned) while owned
            // processing dependencies advance and finished food is rescued.
            if (!System.Object.ReferenceEquals(assembly.Base, null) && (assembly.Base == null || !AssemblyBaseValid(assembly.Base))) return null;
            if (assembly.Plating != null && StageComplete(assembly.Root))
                return SelectStageWork(assembly.Plating) ?? SelectStageWork(assembly.Plating, true);
            // Exhaust work with known routes before probing another allocated
            // dependency. Missing entries in an incomplete catalogue are not
            // evidence that every assigned source is unreachable.
            var work = SelectAssemblyWashing() ?? SelectStageWork(assembly.Root) ?? SelectAssemblyPreparation(false) ??
                SelectStageWork(assembly.Root, true) ?? SelectAssemblyPreparation(true) ?? StoreEmptyAssemblyUtensil();
            var deadline = work == null || work.Storage ? null : AssemblyTripDeadline(work);
            if (deadline == null) return work;
            var cargo = ActionObservations.Held(chef);
            return cargo == null ? deadline : StoreAssemblyCargo(cargo);
        }
        private AssemblyJob StoreEmptyAssemblyUtensil()
        {
            var held = ActionObservations.Held(chef);
            if (held == null || !(assembly.Equipment.Contains(held) || assembly.Components.Contains(held)) ||
                held.GetComponent<ServerIngredientContainer>() == null || !LogisticsInventory.Empty(held) ||
                held.GetComponent<PreparationContainer>() != null || held.GetComponent<ServerPlate>() != null) return null;
            // Returning an empty assigned utensil while food cooks avoids
            // spending the next ready batch's spoil window freeing our hands.
            return StoreAssemblyCargo(held);
        }
        private AssemblyJob StoreAssemblyCargo(GameObject held)
        {
            var counter = AssemblyParking(held, true);
            return counter == null ? null : new AssemblyJob { Stage = assembly.Root, Source = held,
                Destination = counter, Operation = "place_at", Storage = true };
        }
        private GameObject AssemblyParking(GameObject item, bool probeUnknown)
        {
            var excluded = new HashSet<GameObject>();
            foreach (var pair in assembly.RetryAt) if (AssemblyDeferred(pair.Key)) excluded.Add(pair.Key);
            foreach (var key in new List<GameObject>(assembly.RouteRetries.Keys)) if (AssemblyDeferred(key)) excluded.Add(key);
            return Parking(item, excluded: excluded, probeUnknown: probeUnknown);
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
                        var counter = AssemblyParking(stage.Vessel, probeUnknown);
                        if (counter != null) return MoveAssembly(stage, entry, counter);
                    }
                    else
                    {
                        var source = AssemblySource(stage, probeUnknown);
                        // Probing an assigned source does not justify fetching
                        // it for an unknown receiving route. That would shuttle
                        // safe cooked food between hands and storage whenever
                        // another branch is tried. Held cargo can still seek its
                        // own destination without another pickup.
                        if (source != null && !AssemblyDeferred(entry.Holder) && AssemblyApproachCost(entry.Holder, source.Item) != Single.MaxValue)
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
        private bool AssemblyPlanningStalled(ref float since, ref Vector3 position, ref Collider support)
        {
            bool recovering = child.Run.Phase == "recovering_navigation";
            if (!recovering && child.Run.Phase != "navigating") { since = -1; return false; }
            var currentSupport = chef.GroundCollider;
            var current = AssemblyRelativePosition(chef.transform.position, currentSupport);
            // Native walking can keep yielding to another chef while reporting
            // small velocity impulses. Measure useful progress on the support,
            // so neither those impulses nor a moving platform reset the budget.
            if (since < 0 || support != currentSupport || (current - position).sqrMagnitude >= .25f)
            { since = Time.realtimeSinceStartup; position = current; support = currentSupport; }
            return Time.realtimeSinceStartup - since >= (recovering ? 2 : 4);
        }
        private float AssemblyReturnSeconds(GameObject station)
        {
            float distance = inventory.DistanceTo(station);
            if (distance == Single.MaxValue) distance = Vector3.Distance(chef.transform.position, station.transform.position);
            return distance / Math.Max(.1f, chef.Movement.RunSpeed) + 3;
        }
        private AssemblyJob AssemblyTripDeadline(AssemblyJob planned)
        {
            var destination = planned.Destination;
            if (planned.Operation == "pick_component")
            {
                var source = AssemblyEntry(planned.Source);
                if (source == null) return null;
                destination = source.Source;
            }
            float distance = inventory.DistanceTo(destination);
            if (ActionObservations.CanPickUp(chef, destination)) distance = 0;
            // Budget both the outward trip and a return through the current
            // position, plus storage/interaction time. An uncertified outward
            // route cannot justify leaving a cooking batch unattended.
            float trip = distance == Single.MaxValue ? Single.PositiveInfinity :
                2 * distance / Math.Max(.1f, chef.Movement.RunSpeed);
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || stage.Mixing || stage.Vessel == null || !StageFits(stage) || StageComplete(stage) ||
                    !RecipeProof.Equal(StageContents(stage), stage.Inputs)) continue;
                var entry = AssemblyEntry(stage.Vessel);
                if (entry != null && OnProcessor(stage, entry) && !ProcessorOff(entry.Holder, false) &&
                    TaskNative.Remaining(stage.Vessel) <= AssemblyReturnSeconds(entry.Source) + trip)
                    return new AssemblyJob { Stage = stage, Source = stage.Vessel, Destination = entry.Source, Operation = "approach_heat" };
            }
            return null;
        }
        private bool AssemblyHeatDue(bool includeImminent = false)
        {
            foreach (var stage in assembly.Stages)
            {
                if (!stage.Processing || stage.Vessel == null) continue;
                var entry = AssemblyEntry(stage.Vessel);
                if (entry != null && OnProcessor(stage, entry) && (StageComplete(stage) || includeImminent && !stage.Mixing &&
                    StageFits(stage) && RecipeProof.Equal(StageContents(stage), stage.Inputs) && !ProcessorOff(entry.Holder, false) &&
                    TaskNative.Remaining(stage.Vessel) <= AssemblyReturnSeconds(entry.Source))) return true;
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
            if (job.Utility != null)
            {
                foreach (var step in ExecuteAssemblyAccess(job)) yield return step;
                yield break;
            }
            if (job.Preparation != null)
            {
                foreach (var step in ExecuteAssemblyPreparation(job)) yield return step;
                yield break;
            }
            var stage = job.Stage; var item = job.Source; var target = job.Destination;
            assembly.Record(job.Rescue ? "rescue_finished_batch" : job.Storage ? "park_assembly_cargo" : job.Operation, stage, item, target);
            if (job.Operation == "approach_heat")
            {
                float retry = 0;
                yield return new TaskStep { Phase = "approaching_heat", Progress = () => {
                    var entry = AssemblyEntry(item);
                    if (entry == null || !OnProcessor(stage, entry) || !StageFits(stage) || ProcessorOff(entry.Holder, false) ||
                        AssemblyHeatDue() || ActionObservations.Held(chef) != null)
                    { navigation.CancelRecoveryPlanning(); if (navigation.Action.State == "navigating") navigation.Stop("assembly_heat_recheck"); return true; }
                    if (navigation.Action.State == "navigating" || Time.realtimeSinceStartup < retry) return false;
                    if (ActionObservations.CanPickUp(chef, target) && chef.Motion.GetVelocityXZ().magnitude <= .1f) return false;
                    retry = Time.realtimeSinceStartup + .2f;
                    navigation.Start(chef, Run.Owner, navigation.RegisterItem(target), true);
                    return false;
                }};
                yield break;
            }
            Func<bool> valid = () => StageFits(stage);
            string operation = job.Operation;
            var before = StageContents(stage); var addition = RecipeProof.Composition(item);
            if (operation == "pick_component")
            {
                var entry = AssemblyEntry(item); if (entry == null) yield break;
                target = ActionObservations.PickupTarget(entry.Source, item); operation = "pick_up";
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Occupant(job.Destination), stage.Vessel) &&
                    AssemblyTransfer(item, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing);
            }
            else if (operation == "place_at")
                valid = () => (job.Storage ? addition == null ? RecipeProof.Composition(item) == null : RecipeProof.Equal(addition, RecipeProof.Composition(item)) : StageFits(stage)) && System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && ActionObservations.Occupant(target) == null &&
                    (!TaskNative.Equipment(target) || ProcessingStationFor(stage, target) && (stage.Mixing ? RecipeProof.Subset(StageContents(stage), stage.Inputs) : RecipeProof.Equal(RecipeProof.Composition(item), stage.Inputs)));
            else if (operation == "activate")
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Occupant(target), item) &&
                    RecipeProof.Equal(RecipeProof.Composition(item), stage.Inputs) && ProcessorOff(target, stage.Mixing);
            if (operation == "transfer_contents")
                valid = () => StageFits(stage) && System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && System.Object.ReferenceEquals(ActionObservations.Occupant(target), stage.Vessel) &&
                    RecipeProof.Equal(before, StageContents(stage)) && RecipeProof.Equal(addition, RecipeProof.Composition(item)) &&
                    AssemblyTransfer(item, stage.Vessel, stage.Inputs, stage.Processing && stage.Mixing);
            var next = AssemblyStep(operation, target, valid, operation == "transfer_contents" && stage.Processing && stage.Mixing ? "assembly_mixing_inputs" : null);
            var beforePress = next.BeforePress;
            next.BeforePress = () => {
                bool allowed = beforePress() && (job.Rescue || job.Storage || !AssemblyHeatDue(true) && AssemblyTripDeadline(job) == null);
                assembly.Record(allowed ? "press_authorized" : "press_rejected", stage, item, target);
                return allowed;
            };
            if (operation == "pick_up") next.UseHeld = cargo => System.Object.ReferenceEquals(cargo, item);
            // All assembly pickups use the same storage planning and failed-route
            // history. Generic hand clearing can otherwise wait indefinitely for
            // a broad catalogue in a kitchen whose counters keep moving.
            next.FreeHands = ClearAssemblyHands;
            var abandon = next.AbandonBeforePress;
            float planningSince = -1; bool stalled = false; Vector3 planningPosition = Vector3.zero; Collider planningSupport = null;
            next.AbandonBeforePress = () => abandon() || !job.Rescue &&
                (!job.Storage && (AssemblyHeatDue(true) || AssemblyTripDeadline(job) != null) || (stalled = AssemblyPlanningStalled(ref planningSince, ref planningPosition, ref planningSupport)));
            yield return next;
            if (!next.Succeeded)
            {
                assembly.Record("replan_uncompleted_step", stage, item, target);
                var key = operation == "place_at" || operation == "transfer_contents" ? target : item;
                if ((next.RouteFailed || stalled) && !job.Rescue) DeferAssemblyRoute(key, target);
                else assembly.RetryAt[key] = Time.realtimeSinceStartup + (job.Rescue ? .2f : 1);
                yield break;
            }
            assembly.RouteRetries.Remove(operation == "place_at" || operation == "transfer_contents" ? target : item);
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
        private IEnumerable<TaskStep> ClearAssemblyHands(GameObject held)
        {
            while (System.Object.ReferenceEquals(ActionObservations.Held(chef), held))
            {
                var storage = StoreAssemblyCargo(held);
                if (storage == null) { yield return AssemblyWait(AssemblyHeatDue(true) ? "waiting_for_heat_storage" : "waiting_for_assembly_storage"); continue; }
                foreach (var step in ExecuteAssembly(storage)) yield return step;
            }
        }
    }
}
