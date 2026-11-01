using System;
using System.Collections;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Finite logistics jobs compose the native first-layer executor. The caller
    // owns one lease through all child actions; failure never discards cargo.
    internal sealed class LogisticsActions
    {
        public readonly ActionRun Run = new ActionRun();
        public readonly LogisticsInventory Inventory;
        private readonly NavigationActions navigation;
        private readonly InteractionActions child;
        private readonly Action<int> input;
        private readonly Action release;
        private PlayerControls chef;
        private ControlCommand request;
        private LogisticsItem selected;
        private GameObject destination, sourceStation, originalSource, expected, cargo;
        private string destinationId, sourceId, deliveryOperation;
        private int delivered, batchLimit;
        private bool supply;
        public LogisticsActions(NavigationActions nav, InteractionActions primitive, Action<int> buttons, Action finished)
        { navigation = nav; child = primitive; input = buttons; release = finished; Inventory = new LogisticsInventory(nav); }
        public static bool Handles(string operation) { return Array.IndexOf(ActionRun.LogisticsNames, operation) >= 0; }
        public void Reset(string reason) { Cancel(reason); Inventory.Reset(); }
        public object Describe()
        {
            return Run.Describe(Json.Object("sourceId", sourceId, "destinationId", destinationId,
                "sourceInstanceId", originalSource == null ? null : (object)originalSource.GetInstanceID(),
                "cargo", cargo == null ? null : Json.Object("objectInstanceId", cargo.GetInstanceID(), "label", cargo.name),
                "deliveredCount", delivered, "batchLimit", batchLimit, "deliveryOperation", deliveryOperation, "child", child.Describe()));
        }
        public string Start(PlayerControls actor, int owner, ControlCommand command)
        {
            if (Run.Running || child.Run.Running || navigation.Action.State != "idle") return "action_in_progress";
            chef = actor; request = command; Inventory.Refresh(chef);
            selected = null; cargo = expected = originalSource = sourceStation = destination = null;
            sourceId = null; destinationId = command.Destination; delivered = 0; batchLimit = 1;
            string error = Select(); if (error != null) return error;
            if (command.Milliseconds == 0) command.Milliseconds = 120000;
            if (!Run.Start(owner, command, Time.realtimeSinceStartup)) return "action_in_progress";
            if (selected == null) { Finish("succeeded", "station_already_empty"); return null; }
            sourceId = selected.SourceId; sourceStation = selected.Source; originalSource = selected.Item;
            supply = selected.Supply; expected = selected.Item;
            bool cleanStack = selected.Item.GetComponent<CleanPlateStack>() != null;
            if (cleanStack && command.Operation != "get_plate" && command.Operation != "clear_station")
            { Finish("blocked", "source_dispenses_plates_use_get_plate"); return "source_dispenses_plates_use_get_plate"; }
            if (cleanStack)
            {
                var stack = selected.Item.GetComponent<ServerStack>(); expected = stack == null ? null : stack.InspectTopOfStack();
                if (command.Operation == "clear_station" && stack != null) batchLimit = Math.Min(64, stack.GetSize());
                if (expected == null) { Finish("blocked", "source_empty"); return "source_empty"; }
            }
            if (AtDestination(expected)) { Finish("succeeded", "already_at_destination"); return null; }
            GameObject held = ActionObservations.Held(chef);
            if (held != null && !System.Object.ReferenceEquals(held, expected)) { Finish("blocked", "hands_full"); return "hands_full"; }
            error = Delivery(expected);
            if (error != null) { Finish("blocked", error); return error; }
            if (held != null) { cargo = held; return StartDelivery(); }
            return StartPickup();
        }
        private string Select()
        {
            if (request.Operation != "return_utensil")
            {
                destination = navigation.Resolve(destinationId);
                if (destination == null) return "destination_unavailable";
            }
            if (request.Operation == "move_item" || request.Operation == "stage_item" || request.Operation == "return_utensil" || request.Operation == "clear_station")
            {
                GameObject source = navigation.Resolve(request.Source);
                if (source == null) return "source_unavailable";
                if (request.Operation == "clear_station")
                {
                    if (source == destination) return "same_source_destination";
                    if (source.GetComponent<ServerAttachStation>() == null) return "target_is_not_station";
                    if (ActionObservations.Occupant(source) == null) return null;
                    foreach (LogisticsItem candidate in Inventory.Items) if (candidate.Source == source && !candidate.Supply) { selected = candidate; break; }
                }
                else
                    foreach (LogisticsItem candidate in Inventory.Items)
                        if (!candidate.Supply && (candidate.Item == source || candidate.Source == source)) { selected = candidate; break; }
                if (selected == null) return "source_is_not_movable_item";
                if (selected.InUse) return "item_in_use";
                if (request.Operation == "return_utensil")
                {
                    if (selected.UtensilType == null) return "item_is_not_utensil";
                    if (!selected.Empty) return "utensil_has_contents";
                    destination = ReturnDestination(selected.Item);
                    if (destination == null) return "no_available_utensil_home";
                    destinationId = Inventory.Id(destination); request.Destination = destinationId;
                }
            }
            else
            {
                string ingredientId = null; float best = Single.MaxValue;
                foreach (LogisticsItem candidate in Inventory.Items)
                {
                    bool matches = request.Operation == "fetch_ingredient" ? LogisticsInventory.IngredientMatches(candidate, request.Selector) :
                        request.Operation == "get_plate" ? candidate.CleanPlate :
                        candidate.UtensilType != null && candidate.Empty && candidate.UtensilType == LogisticsInventory.CanonicalUtensil(request.Selector);
                    if (!matches || candidate.InUse || candidate.Distance == Single.MaxValue) continue;
                    if (request.Operation == "fetch_ingredient")
                    {
                        if (ingredientId != null && ingredientId != candidate.IngredientId) return "ambiguous_ingredient_use_catalogue_id";
                        ingredientId = candidate.IngredientId;
                    }
                    float score = candidate.Distance + (candidate.Supply ? 10000 : 0);
                    if (score < best) { best = score; selected = candidate; }
                }
                if (selected == null) return request.Operation == "get_plate" ? "no_reachable_clean_plate" : request.Operation == "get_utensil" ? "no_reachable_empty_utensil" : "no_reachable_ingredient";
            }
            return null;
        }
        private GameObject ReturnDestination(GameObject utensil)
        {
            GameObject home = Inventory.Home(utensil), best = null; float score = Single.MaxValue;
            foreach (GameObject station in navigation.Targets())
            {
                if (station == utensil || station.GetComponent<ServerRubbishBin>() != null) continue;
                var attach = station.GetComponent<ServerAttachStation>();
                if (attach == null) continue;
                if (System.Object.ReferenceEquals(attach.InspectItem(), utensil)) return station;
                if (attach.InspectItem() != null || !attach.CanAttachToSelf(utensil, new PlacementContext(PlacementContext.Source.Player))) continue;
                float preference = station == home ? 0 : station.GetComponent<CookingStation>() != null ? 1000 : 2000;
                float distance = Vector3.Distance(station.transform.position, chef.transform.position);
                if (preference + distance < score) { score = preference + distance; best = station; }
            }
            return best;
        }
        private bool AtDestination(GameObject item)
        { return item != null && destination != null && System.Object.ReferenceEquals(ActionObservations.Occupant(destination), item); }
        private string Delivery(GameObject item)
        {
            if (destination == null || !destination.activeInHierarchy) return "destination_unavailable";
            if (destination.GetComponent<ServerRubbishBin>() != null) return "logistics_cannot_discard";
            GameObject occupant = ActionObservations.Occupant(destination);
            var contents = ActionObservations.Container(destination);
            if (request.Operation != "stage_item" && item.GetComponent<IngredientPropertiesComponent>() != null && contents != null)
            {
                var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(item);
                var composition = ActionObservations.Composition(item);
                if (transfer == null || composition == null || !transfer.CanTransferToContainer(contents) || !contents.CanAddIngredient(composition)) return "incompatible_ingredient_destination";
                deliveryOperation = "add_ingredient"; return null;
            }
            if (destination.GetComponent<ServerAttachStation>() == null && destination.GetComponent<ServerWashingStation>() == null) return "destination_is_not_surface";
            if (occupant != null && !(item.GetComponent<ServerPlate>() != null && ActionObservations.StackCount(occupant) > 0)) return "destination_occupied";
            deliveryOperation = "place_at"; return null;
        }
        private string StartPickup()
        {
            Run.Advance("picking_up");
            string error = child.Start(chef, Run.Owner, new ControlCommand { Id = request.Id, Operation = "pick_up", Destination = sourceId });
            if (error != null) Finish("blocked", error); return error;
        }
        private string StartDelivery()
        {
            string error = Delivery(cargo);
            if (error == null)
            {
                Run.Advance("delivering");
                error = child.Start(chef, Run.Owner, new ControlCommand { Id = request.Id, Operation = deliveryOperation, Destination = destinationId });
            }
            if (error != null) Finish("blocked", error); return error;
        }
        public void Tick()
        {
            if (!Run.Running) return;
            try
            {
                if (Run.Expired(Time.realtimeSinceStartup)) { Finish("failed", "logistics_timeout"); return; }
                if (child.Run.Running) return;
                input(0);
                if (child.Run.State != "succeeded") { Finish(child.Run.State == "cancelled" ? "cancelled" : "blocked", child.Run.Reason ?? "child_failed"); return; }
                if (Run.Phase == "picking_up")
                {
                    cargo = ActionObservations.Held(chef);
                    if (cargo == null || (!supply && !System.Object.ReferenceEquals(cargo, expected))) { Finish("blocked", "unexpected_pickup_result"); return; }
                    if (supply)
                    {
                        bool matches;
                        if (selected.IngredientId != null)
                        {
                            var properties = cargo.GetComponent<IngredientPropertiesComponent>();
                            var definition = properties == null ? null : ActionObservations.Read<UnityEngine.Object>(properties, "m_ingredientOrderNode");
                            matches = definition != null && "i" + ActionObservations.Read<int>(definition, "m_uID") == selected.IngredientId;
                        }
                        else matches = selected.UtensilType != null && LogisticsInventory.UtensilType(cargo) == selected.UtensilType && LogisticsInventory.Empty(cargo);
                        if (!matches) { Finish("blocked", "supply_contents_changed"); return; }
                    }
                    StartDelivery(); return;
                }
                if (Run.Phase == "delivering")
                {
                    delivered++;
                    if (request.Operation != "clear_station") { Finish("succeeded", "item_delivered"); return; }
                    if (sourceStation == null || !sourceStation.activeInHierarchy || ActionObservations.Occupant(sourceStation) == null) { Finish("succeeded", "station_cleared"); return; }
                    if (delivered >= batchLimit) { Finish("blocked", "station_repopulated"); return; }
                    if (!System.Object.ReferenceEquals(ActionObservations.Occupant(sourceStation), originalSource)) { Finish("blocked", "station_contents_changed"); return; }
                    var stack = originalSource == null ? null : originalSource.GetComponent<ServerStack>();
                    expected = stack == null ? null : stack.InspectTopOfStack(); cargo = null;
                    if (expected == null) { Finish("blocked", "station_contents_changed"); return; }
                    StartPickup();
                }
            }
            catch (Exception) { Finish("failed", "logistics_observation_unavailable"); }
        }
        public void Cancel(string reason) { if (Run.Running) Finish("cancelled", reason); }
        private void Finish(string state, string reason)
        {
            child.Cancel(reason); Run.Finish(state, reason); release();
        }
        public void Decorate(Json.Record catalog, PlayerControls actor)
        {
            if (!Run.Running && !child.Run.Running) Inventory.Refresh(actor);
            var items = new List<object>(); var ingredients = new List<object>(); var utensils = new List<string>(); var known = new HashSet<string>();
            foreach (LogisticsItem entry in Inventory.Items)
            {
                if (entry.Item == null || entry.Source == null) continue;
                items.Add(entry.Describe());
                if (entry.IngredientId != null && known.Add(entry.IngredientId)) ingredients.Add(Json.Object("id", entry.IngredientId, "label", entry.IngredientName));
                if (entry.UtensilType != null && !utensils.Contains(entry.UtensilType)) utensils.Add(entry.UtensilType);
            }
            catalog["items"] = items; catalog["ingredients"] = ingredients; catalog["utensilTypes"] = utensils;
            if (Run.Running || child.Run.Running || navigation.Action.State != "idle") return;
            var actions = (List<object>)catalog["actions"];
            foreach (string name in ActionRun.LogisticsNames)
            {
                var parameters = Json.Object();
                if (name == "move_item" || name == "stage_item" || name == "return_utensil") parameters["item_id"] = Json.Object("type", "enum", "options", items);
                if (name == "clear_station") parameters["station_id"] = Json.Object("type", "enum", "options", catalog["targets"]);
                if (name == "fetch_ingredient") parameters["ingredient"] = Json.Object("type", "enum", "options", ingredients);
                if (name == "get_utensil") parameters["utensil_type"] = Json.Object("type", "enum", "options", utensils);
                if (name != "return_utensil") parameters["destination_id"] = Json.Object("type", "enum", "options", catalog["targets"]);
                actions.Add(Json.Object("name", name, "parameters", parameters));
            }
        }
    }
}
