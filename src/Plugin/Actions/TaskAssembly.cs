using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private AssemblySchedule assembly;
        partial void RegisterAssembly() { Register("assemble_dish", () => AssemblyOrders.Observe().Count != 0, AssembleDish); }

        private string BindAssembly()
        {
            var allocation = request.Assembly;
            if (allocation == null || request.Repetitions != 1 || allocation.Bases.Count == 0) return "invalid_assembly_allocation";
            var schedule = new AssemblySchedule();
            foreach (var order in AssemblyOrders.Observe()) if (order.Id == allocation.Order) { schedule.Order = order; break; }
            if (schedule.Order == null) return "assembly_order_unavailable";
            var seen = new HashSet<GameObject>();
            for (int group = 0; group < 3; group++)
            {
                var ids = group == 0 ? allocation.Bases : group == 1 ? allocation.Components : allocation.Equipment;
                var objects = group == 0 ? schedule.Bases : group == 1 ? schedule.Components : schedule.Equipment;
                foreach (string id in ids)
                {
                    GameObject item = null;
                    if (group == 2) item = navigation.Resolve(id);
                    else foreach (var entry in inventory.Items)
                        if (entry.ItemId == id && !entry.Supply)
                        {
                            item = entry.Item;
                            var stack = item.GetComponent<ServerStack>();
                            if (stack != null && item.GetComponent<CleanPlateStack>() != null) item = stack.InspectTopOfStack();
                            break;
                        }
                    if (item == null || !seen.Add(item)) return "assembly_resource_unavailable";
                    objects.Add(item);
                }
            }
            assembly = schedule; return null;
        }

        private LogisticsItem AssemblyEntry(GameObject item)
        {
            if (item == null || !item.activeInHierarchy) return null;
            foreach (var entry in inventory.Items)
            {
                if (entry.Supply || entry.InUse) continue;
                if (System.Object.ReferenceEquals(entry.Item, item)) return entry;
                var stack = entry.Item.GetComponent<ServerStack>();
                if (stack != null && entry.Item.GetComponent<CleanPlateStack>() != null && System.Object.ReferenceEquals(stack.InspectTopOfStack(), item)) return entry;
            }
            return null;
        }
        private bool AssemblyBaseValid(GameObject item)
        {
            var plate = item == null ? null : item.GetComponent<ServerPlate>();
            return plate != null && !plate.IsReserved() && plate.GetPlatingStep() == assembly.Order.Recipe.m_platingStep &&
                RecipeProof.Subset(RecipeProof.Composition(item), assembly.Order.Goal);
        }
        private GameObject SelectAssemblyBase()
        {
            GameObject best = null; int progress = -1; float distance = Single.MaxValue;
            foreach (var item in assembly.Bases)
            {
                if (!AssemblyBaseValid(item)) continue;
                var entry = AssemblyEntry(item);
                if (entry == null || !Obtainable(entry, "move_item")) continue;
                int parts = RecipeProof.Parts(RecipeProof.Composition(item)).Count;
                if (parts > progress || parts == progress && entry.Distance < distance)
                { best = item; progress = parts; distance = entry.Distance; }
            }
            return best;
        }
        private bool AssemblyReady(GameObject item)
        {
            if (item == null || TaskNative.Burnt(item)) return false;
            // Raw single ingredients that already have their native definition
            // are usable. Workable (unchopped) stock never substitutes for them.
            if (item.GetComponent<WorkableItem>() != null) return false;
            return (TaskNative.ProcessingHandler(item, false) == null && TaskNative.ProcessingHandler(item, true) == null) || TaskNative.Ready(item);
        }
        private bool AssemblyTransfer(GameObject source, GameObject recipient, AssembledDefinitionNode goal)
        {
            if (!AssemblyReady(source) || recipient == null || !RecipeProof.Advances(RecipeProof.Composition(recipient), RecipeProof.Composition(source), goal)) return false;
            var container = recipient.GetComponent<ServerIngredientContainer>();
            var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(source);
            return container != null && transfer != null && transfer.CanTransferToContainer(container);
        }
        private bool AssemblyDeferred(GameObject item)
        { float at; return assembly.RetryAt.TryGetValue(item, out at) && Time.realtimeSinceStartup < at; }
        private TaskStep AssemblyStep(string operation, GameObject target, Func<bool> proof)
        {
            var next = Reschedulable(operation, target);
            next.BeforePress = () => assembly.Order.Live && proof();
            next.AbandonBeforePress = () => !assembly.Order.Live || !proof() || child.TransferUnavailable;
            return next;
        }
        private IEnumerable<TaskStep> AssembleDish()
        {
            while (!assembly.Verified)
            {
                inventory.Refresh(chef);
                if (System.Object.ReferenceEquals(assembly.Base, null)) assembly.Base = SelectAssemblyBase();
                GameObject plate = assembly.Base;
                var baseEntry = AssemblyEntry(plate);
                if (baseEntry == null || !AssemblyBaseValid(plate))
                { yield return AssemblyWait("waiting_for_assigned_base"); continue; }
                var contents = RecipeProof.Composition(plate);
                if (RecipeProof.Equal(contents, assembly.Order.Goal) && AssembledDefinitionNode.Matching(contents, assembly.Order.Recipe))
                {
                    // Completion is the actual composition and required native
                    // plate type, never the number of presses we attempted.
                    assembly.Verified = true; assembly.Waiting = null; completed = 1;
                    deliveredItems.Add(plate); yield break;
                }
                GameObject held = ActionObservations.Held(chef);
                GameObject destination = baseEntry.Holder;
                if (destination == null || !Counter(destination) || TaskNative.Component<ServerConveyorStation>(destination) != null)
                {
                    GameObject parking = Parking(plate);
                    if (parking == null) { yield return AssemblyWait("waiting_for_assembly_counter"); continue; }
                    if (!System.Object.ReferenceEquals(held, plate))
                    {
                        var pickup = AssemblyStep("pick_up", baseEntry.Source, () => AssemblyBaseValid(plate));
                        pickup.UseHeld = cargo => System.Object.ReferenceEquals(cargo, plate);
                        yield return pickup;
                    }
                    else
                    {
                        var put = AssemblyStep("place_at", parking, () => System.Object.ReferenceEquals(ActionObservations.Held(chef), plate) &&
                            AssemblyBaseValid(plate) && ActionObservations.Occupant(parking) == null);
                        yield return put;
                    }
                    continue;
                }
                LogisticsItem source = null; float cost = Single.MaxValue;
                foreach (GameObject item in assembly.Components)
                {
                    if (AssemblyDeferred(item) || !AssemblyTransfer(item, plate, assembly.Order.Goal)) continue;
                    var entry = AssemblyEntry(item);
                    if (entry == null || !Obtainable(entry, "move_item")) continue;
                    float candidate = System.Object.ReferenceEquals(held, item) ? -1 : entry.Distance;
                    if (candidate < cost) { cost = candidate; source = entry; }
                }
                if (source == null) { yield return AssemblyWait("waiting_for_assigned_component"); continue; }
                GameObject food = source.Item;
                Func<bool> permitted = () => AssemblyBaseValid(plate) && System.Object.ReferenceEquals(ActionObservations.Occupant(destination), plate) &&
                    AssemblyTransfer(food, plate, assembly.Order.Goal);
                if (!System.Object.ReferenceEquals(held, food))
                {
                    var pickup = AssemblyStep("pick_up", ActionObservations.PickupTarget(source.Source, food), permitted);
                    pickup.UseHeld = cargo => System.Object.ReferenceEquals(cargo, food);
                    yield return pickup;
                    if (!pickup.Succeeded) assembly.RetryAt[food] = Time.realtimeSinceStartup + 1;
                    continue;
                }
                var before = RecipeProof.Composition(plate); var addition = RecipeProof.Composition(food);
                var add = AssemblyStep("transfer_contents", destination, () => permitted() &&
                    RecipeProof.Equal(before, RecipeProof.Composition(plate)) && RecipeProof.Equal(addition, RecipeProof.Composition(food)));
                yield return add;
                if (!add.Succeeded) { assembly.RetryAt[food] = Time.realtimeSinceStartup + 1; continue; }
                if (!RecipeProof.Equal(RecipeProof.Combine(before, addition), RecipeProof.Composition(plate)))
                    throw new InvalidOperationException("assembly_composition_changed_after_transfer");
                assembly.Additions++;
            }
        }
        private TaskStep AssemblyWait(string reason)
        {
            assembly.Waiting = reason; float retry = Time.realtimeSinceStartup + .2f;
            return Until(reason, () => Time.realtimeSinceStartup >= retry);
        }
    }
}
