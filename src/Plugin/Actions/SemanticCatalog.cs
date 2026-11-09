using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Independent observation state: never touch the active task's request,
    // iterator, reservations, inventory snapshot, input or route planner.
    internal sealed class SemanticCatalog
    {
        private readonly NavigationActions navigation;
        private readonly LogisticsInventory inventory;
        public SemanticCatalog(NavigationActions nav) { navigation = nav; inventory = new LogisticsInventory(nav); }
        public void Reset() { inventory.Reset(); }
        public object Capture(PlayerControls actor, object players)
        {
            navigation.ObserveTargets(actor);
            var catalog = Json.Object("destinations", new List<object>(), "complete", false);
            inventory.Refresh(actor, catalog); // Empty route observations mean unknown, never unreachable.
            HarnessOptions.Decorate(catalog, navigation, inventory);
            var targets = new List<object>();
            var kitchenTargets = navigation.Targets();
            foreach (GameObject target in kitchenTargets)
            {
                var identity = (Json.Record)navigation.TargetIdentity(target);
                try { HarnessOptions.Target(identity, target); }
                catch (Exception) { identity["observationError"] = "target_observation_unavailable"; }
                targets.Add(identity);
            }
            var pendingOrders = new List<object>();
            var assemblyOrders = new List<object>();
            AssemblyResources resources = null;
            foreach (var order in AssemblyOrders.Observe(includeUnsupported: true))
            {
                pendingOrders.Add(AssemblyOrders.DescribeTicket(order));
                if (order.Goal == null) continue;
                if (resources == null) resources = new AssemblyResources(inventory, kitchenTargets);
                var recipe = AssemblyOrders.DescribeRecipe(order);
                recipe["allocation"] = resources.Allocate(order);
                assemblyOrders.Add(recipe);
            }
            return Json.Object("version", CapabilityRegistry.Version, "observedAt", Time.realtimeSinceStartup,
                "routeCoverage", "unknown", "players", players, "targets", targets,
                "movement", navigation.GroundedMap(actor),
                "displayOptions", catalog["displayOptions"], "pendingOrders", pendingOrders, "assemblyOrders", assemblyOrders,
                "observationErrors", inventory.Errors);
        }

        public static object Preparation(PreparationIngredient root)
        {
            var chain = new List<object>(); var seen = new HashSet<GameObject>();
            GameObject stage = root.Prefab; bool complete = true;
            while (stage != null)
            {
                if (!seen.Add(stage) || chain.Count >= 32) { complete = false; break; }
                string id, label;
                if (!LogisticsInventory.IngredientIdentity(stage, out id, out label)) complete = false;
                chain.Add(Json.Object("ingredientId", id, "label", label));
                var work = stage.GetComponent<WorkableItem>();
                if (work == null) break;
                stage = work.GetNextPrefab();
                if (stage == null) complete = false;
            }
            // Cooking/mixing needs recipe and vessel compatibility. A handler
            // or an enum value alone is not proof; leave it explicitly unknown.
            return Json.Object("id", root.Id, "label", root.Name, "utensil", false,
                "workChain", chain, "workChainComplete", complete,
                "preparations", Json.Object("raw", "applicable", "chopped", !complete ? "unknown" : chain.Count > 1 ? "applicable" : "not_applicable",
                    "cooked", "unknown", "mixed", "unknown", "empty", "not_applicable"));
        }
    }
}
