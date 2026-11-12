using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private float assignedOrderSeconds = Single.MaxValue;
        private sealed class Demand
        {
            public AssembledDefinitionNode Ingredient;
            public string Processing;
            public float Seconds;
        }
        // Allocate each existing output and each requested portion once, oldest
        // ticket first. Processing boundaries are matched before descending to
        // ingredients, so raw stock cannot satisfy a cooked recipe output.
        internal static void AssignKitchenDemand(PlayerControls actor, IList<TaskActions> jobs)
        {
            foreach (var job in jobs) job.assignedOrderSeconds = Single.MaxValue;
            if (jobs.Count == 0) return;
            var observer = jobs[0]; observer.inventory.Refresh(actor);
            var stock = new List<AssembledDefinitionNode>(); var seen = new HashSet<GameObject>();
            foreach (var item in observer.inventory.Items)
            {
                if (item.Supply || item.Item == null || !seen.Add(item.Item) || TaskNative.Burnt(item.Item)) continue;
                bool owned = false;
                foreach (var job in jobs) if (job.Owns(item.Item)) { owned = true; break; }
                if (owned) continue;
                if (TaskNative.Equipment(item.Holder) && !TaskNative.ReadyAt(item.Holder)) continue;
                var composition = RecipeProof.Composition(item.Item);
                if (composition != null && !RecipeProof.Empty(composition)) stock.AddRange(RecipeProof.Parts(composition));
            }
            var orders = AssemblyOrders.Observe();
            orders.Sort((a, b) => (a.Expires ? a.Ticket.Remaining : Single.MaxValue).CompareTo(b.Expires ? b.Ticket.Remaining : Single.MaxValue));
            var demand = new List<Demand>();
            foreach (var order in orders)
                MissingDemand(order.Goal, null, order.Expires ? order.Ticket.Remaining : Single.MaxValue, stock, demand);
            var identities = new Dictionary<string, HashSet<string>>();
            foreach (var need in demand)
            {
                TaskActions chosen = null;
                foreach (var job in jobs)
                {
                    if (job.request == null || job.request.Action != "prepare_near_player" || job.request.Items.Count != 1 ||
                        job.assignedOrderSeconds != Single.MaxValue) continue;
                    var desired = job.request.Items[0];
                    if (need.Processing != null && Passive(desired) && desired.Preparation != need.Processing) continue;
                    if (need.Processing == null && Passive(desired)) continue;
                    HashSet<string> matches;
                    if (!identities.TryGetValue(desired.Selector, out matches)) identities.Add(desired.Selector, matches = observer.PreparationIdentities(desired.Selector, true));
                    var ingredient = need.Ingredient as IngredientAssembledNode;
                    if (ingredient == null || !matches.Contains("i" + ingredient.m_ingriedientOrderNode.m_uID)) continue;
                    if (chosen == null || job.ownedFood.Count > chosen.ownedFood.Count) chosen = job;
                }
                if (chosen != null) chosen.assignedOrderSeconds = need.Seconds;
            }
        }
        private static void MissingDemand(AssembledDefinitionNode node, string processing, float seconds,
            List<AssembledDefinitionNode> stock, List<Demand> demand)
        {
            int found = processing == null ? stock.FindIndex(item => RecipeProof.Equal(item, node)) : -1;
            if (found >= 0) { stock.RemoveAt(found); return; }
            var composite = node as CompositeAssembledNode;
            if (composite != null)
            {
                if (node is CookedCompositeAssembledNode) processing = "cooked";
                else if (node is MixedCompositeAssembledNode) processing = "mixed";
                foreach (var child in composite.m_composition) MissingDemand(child, processing, seconds, stock, demand);
            }
            else if (node is IngredientAssembledNode) demand.Add(new Demand { Ingredient = node, Processing = processing, Seconds = seconds });
        }
    }
}
