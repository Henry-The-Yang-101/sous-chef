using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Display choices are observations of this kitchen, never permission to act.
    // Keep the existing live operations/jobOptions/taskOptions as the authority.
    internal static class HarnessOptions
    {
        public static void Target(Json.Record identity, GameObject go)
        {
            var operations = new List<string> { "navigate_to", "wait_until" };
            bool bin = go.GetComponent<ServerRubbishBin>() != null;
            bool holder = go.GetComponent<ServerAttachStation>() != null;
            bool sink = go.GetComponent<ServerWashingStation>() != null;
            bool work = sink || go.GetComponent<ServerWorkstation>() != null;
            bool placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(go) != null;
            if (PlayerControlsHelper.GetControllingPickupHandler_Client(go) != null) operations.Add("pick_up");
            if (placement) operations.Add(bin ? "discard" : "place_at");
            if (!bin && (holder || ActionObservations.Container(go) != null))
            { operations.Add("add_ingredient"); operations.Add("collect_contents"); }
            if (work) operations.Add("work_at");
            if (go.GetComponent<ClientInteractable>() != null && !work) operations.Add("activate");
            if (!bin && (holder || GameObjectUtils.RequestInterface<IHandleCatch>(ActionObservations.Receiver(go)) != null)) operations.Add("throw_to");
            var workConditions = new List<string>(InteractionActions.ConditionsFor(go, true));
            if (work && !sink && !workConditions.Contains("work_complete")) workConditions.Add("work_complete");
            var waitConditions = new List<string>(InteractionActions.ConditionsFor(go, false));
            foreach (string condition in workConditions) if (!waitConditions.Contains(condition)) waitConditions.Add(condition);
            if (go.GetComponent<CookingStation>() != null)
                foreach (string condition in new[] { "cooked", "mixed" }) if (!waitConditions.Contains(condition)) waitConditions.Add(condition);
            identity["possibleOperations"] = operations.ToArray();
            identity["possibleWorkConditions"] = workConditions.ToArray();
            identity["possibleWaitConditions"] = waitConditions.ToArray();
        }

        public static void Decorate(Json.Record catalog, NavigationActions navigation, LogisticsInventory inventory)
        {
            var items = new List<object>(); var ingredients = new List<object>(); var utensils = new List<string>();
            var preparation = new List<object>(); var destinations = new List<object>();
            var ingredientIds = new HashSet<string>(); var roots = new HashSet<string>();
            foreach (LogisticsItem item in inventory.Items)
            {
                if (!item.Supply) items.Add(item.Describe());
                if (item.IngredientId != null && ingredientIds.Add(item.IngredientId))
                    ingredients.Add(Json.Object("id", item.IngredientId, "label", item.IngredientName));
                string type = item.CleanPlate ? "plate" : item.UtensilType;
                if (type != null && !utensils.Contains(type)) utensils.Add(type);
            }
            // Supplies remain selectable regardless of route, free hands, or the
            // current contents of a station. No global ingredient registry is used.
            foreach (GameObject go in navigation.Targets())
            {
                var spawner = go.GetComponent<PickupItemSpawner>();
                string id, label;
                if (spawner != null && LogisticsInventory.IngredientIdentity(spawner.m_itemPrefab, out id, out label))
                {
                    if (roots.Add(id)) preparation.Add(Json.Object("id", id, "label", label, "utensil", false));
                    if (ingredientIds.Add(id)) ingredients.Add(Json.Object("id", id, "label", label));
                }
                if (PlayerControlsHelper.GetControllingPlacementHandler_Client(go) != null)
                    destinations.Add(navigation.TargetIdentity(go));
            }
            foreach (string type in utensils) preparation.Add(Json.Object("id", type, "label", type, "utensil", true));
            catalog["displayOptions"] = Json.Object("items", items, "ingredients", ingredients, "utensilTypes", utensils,
                "preparationOptions", preparation, "deliveryDestinations", destinations);
        }
    }
}
