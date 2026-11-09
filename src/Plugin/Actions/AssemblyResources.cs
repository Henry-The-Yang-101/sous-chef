using System;
using System.Collections.Generic;
using System.Linq;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Read-only proposals for callers that select a ticket without supplying
    // resources. The executor still owns route, transfer and input checks.
    internal sealed class AssemblyResources
    {
        private sealed class Stock
        {
            public string Id;
            public GameObject Item;
            public AssembledDefinitionNode Composition;
            public bool Plate, PreparationBase, Utensil, Transferable;
        }

        private readonly List<Stock> stock = new List<Stock>();
        private readonly List<GameObject> stations;
        private readonly LogisticsInventory inventory;

        public AssemblyResources(LogisticsInventory inventory, List<GameObject> targets)
        {
            this.inventory = inventory;
            stations = targets.FindAll(TaskNative.Equipment);
            var seen = new HashSet<GameObject>();
            foreach (var entry in inventory.Items)
            {
                if (entry.Supply || entry.InUse || entry.Item == null) continue;
                var item = LogisticsChecks.Payload(entry);
                if (item == null || !seen.Add(item)) continue;
                try
                {
                    var composition = RecipeProof.Composition(item);
                    if (!RecipeProof.Supported(composition) || TaskNative.Burnt(item)) continue;
                    stock.Add(new Stock { Id = entry.ItemId, Item = item, Composition = composition,
                        Plate = item.GetComponent<ServerPlate>() != null, PreparationBase = item.GetComponent<PreparationContainer>() != null,
                        Utensil = entry.UtensilType != null, Transferable = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(item) != null });
                }
                catch (Exception) { inventory.Errors.Add(Json.Object("sourceId", entry.SourceId, "reason", "assembly_resource_observation_unavailable")); }
            }
        }

        public object Allocate(AssemblyOrder order)
        {
            var recipe = new AssemblySchedule { Order = order };
            recipe.Initialize();
            var components = new List<string>();
            var equipment = new List<string>();
            var stockParts = new List<AssembledDefinitionNode>();
            var vesselStages = new HashSet<AssemblySchedule.Stage>();
            var processing = recipe.Stages.FindAll(stage => stage.Processing);
            foreach (var source in stock)
            {
                if (source.Plate) continue;
                bool useful = source.Transferable && !RecipeProof.Empty(source.Composition) && Ready(source.Item, source.Composition) &&
                    recipe.Stages.Any(stage => RecipeProof.Subset(source.Composition, stage.Inputs));
                var compatible = source.PreparationBase || source.Utensil ? processing.FindAll(stage => VesselFor(stage, source.Item)) : null;
                bool vessel = compatible != null && compatible.Any(stage => useful || RecipeProof.Subset(ContentsFor(stage, source.Item), stage.Inputs));
                // An allocated vessel can continue into a parent stage or be
                // reused after the executor transfers its first batch.
                if (vessel) vesselStages.UnionWith(compatible);
                if (vessel && !source.PreparationBase) equipment.Add(source.Id);
                else if (useful || vessel) components.Add(source.Id);
                else continue;
                stockParts.AddRange(RecipeProof.Parts(source.Composition));
            }
            var processors = new HashSet<AssemblySchedule.Stage>();
            foreach (var station in stations)
            {
                var supported = processing.FindAll(stage => vesselStages.Contains(stage) && StationFor(stage, station));
                if (supported.Count == 0) continue;
                processors.UnionWith(supported);
                equipment.Add(inventory.Id(station));
            }

            // Count exact ingredient identities once per observed object. This
            // checks stock and processing prerequisites before offering a base.
            var demand = new Dictionary<int, int>();
            CountIngredients(order.Goal, demand);
            var bases = new List<string>();
            foreach (var plate in stock)
            {
                if (!plate.Plate || !PlateFits(order, plate.Item, plate.Composition)) continue;
                var available = new List<AssembledDefinitionNode>(stockParts);
                available.AddRange(RecipeProof.Parts(plate.Composition));
                var quantities = new Dictionary<int, int>();
                foreach (var part in available) CountIngredients(part, quantities);
                if (demand.All(pair => quantities.TryGetValue(pair.Key, out int count) && count >= pair.Value) &&
                    HasProcessors(recipe.Root, available, processors)) bases.Add(plate.Id);
            }
            if (bases.Count == 0 || new[] { bases, components, equipment }.Any(group => group.Count > CapabilityRegistry.MaxAllocationGroup)) return null;
            return Json.Object("bases", bases, "components", components, "equipment", equipment);
        }

        private static void CountIngredients(AssembledDefinitionNode node, Dictionary<int, int> counts)
        {
            if (node is IngredientAssembledNode ingredient)
            {
                int id = ingredient.m_ingriedientOrderNode.m_uID;
                counts.TryGetValue(id, out int count);
                counts[id] = count + 1;
            }
            else if (node is CompositeAssembledNode composite)
                foreach (var part in composite.m_composition) CountIngredients(part, counts);
        }

        private static bool HasProcessors(AssemblySchedule.Stage parent, List<AssembledDefinitionNode> available, HashSet<AssemblySchedule.Stage> processors)
        {
            foreach (var stage in parent.Children)
            {
                int existing = available.FindIndex(part => AssembledDefinitionNode.Matching(part, stage.Goal));
                if (existing >= 0) available.RemoveAt(existing);
                else if (!processors.Contains(stage) || !HasProcessors(stage, available, processors)) return false;
            }
            return true;
        }

        public static bool PlateFits(AssemblyOrder order, GameObject item, AssembledDefinitionNode composition = null)
        {
            var plate = TaskNative.Component<ServerPlate>(item);
            return plate != null && !plate.IsReserved() && plate.GetPlatingStep() == order.Recipe.m_platingStep &&
                RecipeProof.Subset(composition ?? RecipeProof.Composition(item), order.Goal);
        }

        public static AssembledDefinitionNode ContentsFor(AssemblySchedule.Stage stage, GameObject item)
        { return stage.Processing && stage.Mixing ? RecipeProof.MixingInputs(item) : RecipeProof.Composition(item); }

        public static bool VesselFor(AssemblySchedule.Stage stage, GameObject item)
        {
            if (item == null || item.GetComponent<ServerIngredientContainer>() == null || TaskNative.ProcessingHandler(item, stage.Mixing) == null) return false;
            if (!(stage.Goal is CookedCompositeAssembledNode cooked)) return true;
            var handler = item.GetComponent<CookingHandler>();
            var step = handler == null ? null : ActionObservations.Read<CookingStepData>(handler, "m_cookingType");
            return step != null && cooked.m_cookingStep != null && step.m_uID == cooked.m_cookingStep.m_uID;
        }

        public static bool StationFor(AssemblySchedule.Stage stage, GameObject station)
        { return TaskNative.Equipment(station) && stage.Mixing == TaskNative.IsMixer(station); }

        public static bool Ready(GameObject item, AssembledDefinitionNode composition = null)
        {
            // Native ingredient definitions admit raw rice and flour. Choppable
            // stock does not substitute for its finished ingredient.
            if (item == null || item.GetComponent<WorkableItem>() != null || TaskNative.Burnt(item)) return false;
            if (TaskNative.ProcessingHandler(item, false) == null && TaskNative.ProcessingHandler(item, true) == null) return true;
            composition = composition ?? RecipeProof.Composition(item);
            if (composition is CookedCompositeAssembledNode) return TaskNative.Ready(item, false);
            if (composition is MixedCompositeAssembledNode) return TaskNative.Ready(item, true);
            return false;
        }
    }
}
