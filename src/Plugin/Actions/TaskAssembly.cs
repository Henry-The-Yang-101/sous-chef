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
                        if (entry.ItemId == id && !entry.Supply) { item = LogisticsChecks.Payload(entry); break; }
                    if (item == null || !seen.Add(item)) return "assembly_resource_unavailable";
                    objects.Add(item);
                }
            }
            schedule.Initialize(); assembly = schedule; return null;
        }

        private LogisticsItem AssemblyEntry(GameObject item)
        {
            if (item == null || !item.activeInHierarchy || item.GetComponent<ServerPlate>() != null && item.GetComponent<ServerPlate>().IsReserved()) return null;
            foreach (var entry in inventory.Items)
            {
                // Native additions/conveyors can destroy a cached item between
                // observation and the next pre-press heat check.
                if (entry.Item == null || entry.Supply || entry.InUse) continue;
                if (System.Object.ReferenceEquals(entry.Item, item)) return entry;
                var stack = entry.Item.GetComponent<ServerStack>();
                if (stack != null && entry.Item.GetComponent<CleanPlateStack>() != null && System.Object.ReferenceEquals(stack.InspectTopOfStack(), item)) return entry;
            }
            return null;
        }
        private bool AssemblyBaseValid(GameObject item)
        { return AssemblyResources.PlateFits(assembly.Order, item); }
        private bool AssemblyObtainable(LogisticsItem entry, string operation, bool probeUnknown)
        {
            return Obtainable(entry, operation) || probeUnknown && !(bool)inventory.Catalog["complete"] &&
                LogisticsChecks.Collect(chef, entry, operation, true) == "source_unreachable" &&
                LogisticsChecks.CanCollectEmpty(chef, entry.Source);
        }
        private GameObject SelectAssemblyBase()
        {
            GameObject best = null; int progress = -1; float distance = Single.MaxValue;
            foreach (var item in assembly.Bases)
            {
                if (!AssemblyBaseValid(item)) continue;
                var entry = AssemblyEntry(item);
                if (entry == null || !AssemblyObtainable(entry, entry.CleanPlate ? "get_plate" : "move_item", true)) continue;
                float cost = AssemblyApproachCost(entry.Source, item, true);
                int parts = RecipeProof.Parts(RecipeProof.Composition(item)).Count;
                if (parts > progress || parts == progress && cost < distance)
                { best = item; progress = parts; distance = cost; }
            }
            return best;
        }
        private void SelectAssemblyWorkspace()
        {
            if (assembly.Root.Vessel != null || assembly.Base == null) return;
            assembly.Root.Vessel = assembly.Base;
            // Keep a partially plated dish intact. For an empty plate, an
            // assigned preparation base retains the native addition rules and
            // is plated only once its complete recipe has been assembled.
            if (!RecipeProof.Empty(RecipeProof.Composition(assembly.Base))) return;
            GameObject best = null; int progress = -1; float distance = Single.MaxValue;
            var candidates = new List<GameObject>(assembly.Bases); candidates.AddRange(assembly.Components);
            foreach (var item in candidates)
            {
                if (item == null || item.GetComponent<PreparationContainer>() == null || !AssemblyResources.Ready(item) ||
                    !RecipeProof.Subset(RecipeProof.Composition(item), assembly.Order.Goal)) continue;
                var entry = AssemblyEntry(item);
                if (entry == null || !AssemblyObtainable(entry, "move_item", true)) continue;
                float cost = AssemblyApproachCost(entry.Source, item, true);
                int parts = RecipeProof.Parts(RecipeProof.Composition(item)).Count;
                if (parts > progress || parts == progress && cost < distance)
                { best = item; progress = parts; distance = cost; }
            }
            if (best == null) return;
            assembly.Root.Vessel = best;
            assembly.Plating = new AssemblySchedule.Stage { Goal = assembly.Order.Goal, Inputs = assembly.Order.Goal, Vessel = assembly.Base };
            assembly.Stages.Add(assembly.Plating);
        }
        private bool AssemblyTransfer(GameObject source, GameObject recipient, AssembledDefinitionNode goal, bool mixingInputs = false)
        {
            if (!AssemblyResources.Ready(source) || recipient == null || !RecipeProof.Advances(mixingInputs ? RecipeProof.MixingInputs(recipient) : RecipeProof.Composition(recipient), RecipeProof.Composition(source), goal)) return false;
            var container = recipient.GetComponent<ServerIngredientContainer>();
            var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(source);
            if (container == null || transfer == null || !transfer.CanTransferToContainer(container)) return false;
            // Plating may forward into a preparation base inside the plate.
            // Its native recipe/prefab constraints can reject a structurally
            // valid intermediate, even though the generic container accepts it.
            var plate = recipient.GetComponent<ClientPlate>();
            return plate == null || (bool)TaskNative.Call(plate, "CanPlaceOnPlate", source, new PlacementContext(PlacementContext.Source.Player));
        }
        private bool AssemblyDeferred(GameObject item)
        {
            float at;
            if (assembly.RetryAt.TryGetValue(item, out at) && Time.realtimeSinceStartup < at) return true;
            AssemblySchedule.RouteRetry retry;
            if (!assembly.RouteRetries.TryGetValue(item, out retry)) return false;
            if (AssemblyRouteChanged(retry)) { assembly.RouteRetries.Remove(item); return false; }
            return Time.realtimeSinceStartup < retry.After;
        }
        private Vector3 AssemblyRelativePosition(Vector3 point, Collider support)
        { return support == null ? point : support.transform.InverseTransformPoint(point); }
        private bool AssemblyRouteChanged(AssemblySchedule.RouteRetry retry)
        {
            var support = chef.GroundCollider;
            return retry.Target == null || support != retry.Support ||
                (AssemblyRelativePosition(chef.transform.position, support) - retry.ActorPosition).sqrMagnitude >= .16f ||
                (AssemblyRelativePosition(retry.Target.transform.position, support) - retry.TargetPosition).sqrMagnitude >= .16f ||
                !System.Object.ReferenceEquals(ActionObservations.Held(chef), retry.Held) ||
                !System.Object.ReferenceEquals(ActionObservations.Occupant(retry.Target), retry.Occupant) ||
                !retry.Reachable && inventory.DistanceTo(retry.Target) != Single.MaxValue;
        }
        private void DeferAssemblyRoute(GameObject key, GameObject target)
        {
            AssemblySchedule.RouteRetry retry;
            if (!assembly.RouteRetries.TryGetValue(key, out retry) || retry.Target != target || AssemblyRouteChanged(retry))
                retry = new AssemblySchedule.RouteRetry();
            retry.Attempts++;
            retry.After = Time.realtimeSinceStartup + (float)Math.Pow(2, Math.Min(4, retry.Attempts));
            retry.Target = target; retry.Support = chef.GroundCollider;
            retry.ActorPosition = AssemblyRelativePosition(chef.transform.position, retry.Support);
            retry.TargetPosition = AssemblyRelativePosition(target.transform.position, retry.Support);
            retry.Held = ActionObservations.Held(chef); retry.Occupant = ActionObservations.Occupant(target);
            retry.Reachable = inventory.DistanceTo(target) != Single.MaxValue;
            assembly.RouteRetries[key] = retry;
            assembly.Record("route_deferred", source: key, destination: target);
        }
        private TaskStep AssemblyStep(string operation, GameObject target, Func<bool> proof, string condition = null)
        {
            var next = Reschedulable(operation, target, condition);
            var ready = next.Ready;
            next.Ready = () => assembly.Order.Live && proof() && (ready == null || ready());
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
                SelectAssemblyWorkspace();
                var plate = assembly.Base;
                if (AssemblyEntry(plate) != null && AssemblyBaseValid(plate))
                {
                    var contents = RecipeProof.Composition(plate);
                    if (RecipeProof.Equal(contents, assembly.Order.Goal) && AssembledDefinitionNode.Matching(contents, assembly.Order.Recipe))
                    {
                        assembly.Verified = true; assembly.Waiting = null; completed = 1;
                        assembly.Record("composition_verified", assembly.Root, null, plate);
                        deliveredItems.Add(plate); yield break;
                    }
                }
                var job = SelectAssemblyWork();
                if (job == null) { yield return AssemblyWait(assembly.Waiting ?? (plate == null ? "waiting_for_assigned_base" : "waiting_for_assembly_dependency")); continue; }
                assembly.Waiting = null;
                foreach (var next in ExecuteAssembly(job)) yield return next;
            }
        }
        private TaskStep AssemblyWait(string reason)
        {
            assembly.Record(reason);
            assembly.Waiting = reason; float retry = Time.realtimeSinceStartup + .2f;
            return Until(reason, () => Time.realtimeSinceStartup >= retry);
        }
    }
}
