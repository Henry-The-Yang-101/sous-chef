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
        private static string Utensil(GameObject item)
        {
            return item.GetComponent<ServerPlate>() != null || item.GetComponent<ServerDirtyPlateStack>() != null ||
                item.GetComponent<CleanPlateStack>() != null ? "plate" : LogisticsInventory.UtensilType(item);
        }
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
            if (ActionObservations.ActivationTarget(go)) operations.Add("activate");
            if (!bin && (holder || GameObjectUtils.RequestInterface<IHandleCatch>(ActionObservations.Receiver(go)) != null)) operations.Add("throw_to");
            var workConditions = new List<string>(InteractionActions.ConditionsFor(go, true));
            if (work && !sink && !workConditions.Contains("work_complete")) workConditions.Add("work_complete");
            var waitConditions = new List<string>(InteractionActions.ConditionsFor(go, false));
            foreach (string condition in workConditions) if (!waitConditions.Contains(condition)) waitConditions.Add(condition);
            if (go.GetComponent<CookingStation>() != null)
                foreach (string condition in new[] { "cooked", "mixed" }) if (!waitConditions.Contains(condition)) waitConditions.Add(condition);
            identity["processingEquipment"] = TaskNative.Equipment(go);
            identity["possibleOperations"] = operations.ToArray();
            identity["activationStates"] = ActionObservations.ActivationStates(go);
            identity["possibleWorkConditions"] = workConditions.ToArray();
            identity["possibleWaitConditions"] = waitConditions.ToArray();
        }

        public static void Decorate(Json.Record catalog, NavigationActions navigation, LogisticsInventory inventory)
        {
            var items = new List<object>(); var ingredients = new List<object>(); var utensils = new List<string>();
            var preparation = new List<object>(); var destinations = new List<object>();
            var ingredientIds = new HashSet<string>();
            var seenItems = new HashSet<int>();
            foreach (LogisticsItem item in inventory.Items)
            {
                if (!item.Supply) { items.Add(item.Describe()); seenItems.Add(item.Item.GetInstanceID()); }
                if (item.IngredientId != null && ingredientIds.Add(item.IngredientId))
                    ingredients.Add(Json.Object("id", item.IngredientId, "label", item.IngredientName));
                string type = Utensil(item.Item);
                if (type != null && !utensils.Contains(type)) utensils.Add(type);
            }
            foreach (GameObject item in inventory.OtherHeldItems)
            {
                if (item == null) continue;
                string type = Utensil(item), ingredientId, ingredientName;
                if (type != null && !utensils.Contains(type)) utensils.Add(type);
                LogisticsInventory.IngredientIdentity(item, out ingredientId, out ingredientName);
                if (ingredientId != null && ingredientIds.Add(ingredientId)) ingredients.Add(Json.Object("id", ingredientId, "label", ingredientName));
                if (!seenItems.Add(item.GetInstanceID())) continue;
                string id = navigation.RegisterItem(item);
                items.Add(Json.Object("id", id, "label", item.name, "objectInstanceId", item.GetInstanceID(),
                    "utensilType", type == "plate" ? null : type, "inUse", true));
            }
            // Include all configured outputs, even between automatic spawns.
            // This is a type choice, never a claim that cargo is obtainable now.
            foreach (PreparationIngredient root in inventory.IngredientRoots)
            {
                preparation.Add(SemanticCatalog.Preparation(root));
                if (ingredientIds.Add(root.Id)) ingredients.Add(Json.Object("id", root.Id, "label", root.Name));
            }
            foreach (GameObject go in navigation.Targets())
            {
                if (PlayerControlsHelper.GetControllingPlacementHandler_Client(go) != null)
                    destinations.Add(navigation.TargetIdentity(go));
            }
            foreach (string type in utensils) preparation.Add(Json.Object("id", type, "label", type, "utensil", true, "preparations",
                Json.Object("raw", "not_applicable", "chopped", "not_applicable", "cooked", "not_applicable", "mixed", "not_applicable", "empty", "applicable")));
            catalog["displayOptions"] = Json.Object("items", items, "ingredients", ingredients, "utensilTypes", utensils,
                "preparationOptions", preparation, "deliveryDestinations", destinations);
        }
    }
}
