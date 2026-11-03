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
        private bool spawnedChild;
        private bool pendingStart;
        private readonly ActionRecovery recovery = new ActionRecovery();
        private readonly Queue<GameObject> plates = new Queue<GameObject>();
        public LogisticsActions(NavigationActions nav, InteractionActions primitive, Action<int> buttons, Action finished)
        { navigation = nav; child = primitive; input = buttons; release = finished; Inventory = new LogisticsInventory(nav); }
        public static bool Handles(string operation) { return Array.IndexOf(ActionRun.LogisticsNames, operation) >= 0; }
        public void Reset(string reason) { Cancel(reason); Inventory.Reset(); }
        public object Describe()
        {
            return Run.Describe(Json.Object("sourceId", sourceId, "destinationId", destinationId,
                "sourceInstanceId", originalSource == null ? null : (object)originalSource.GetInstanceID(),
                "cargo", cargo == null ? null : Json.Object("objectInstanceId", cargo.GetInstanceID(), "label", cargo.name),
                "deliveredCount", delivered, "batchLimit", batchLimit, "deliveryOperation", deliveryOperation,
                "recovery", recovery.Describe(), "child", spawnedChild ? child.Describe() : null));
        }
        public string Start(PlayerControls actor, int owner, ControlCommand command)
        {
            if (Run.Running || child.Run.Running || navigation.Action.State != "idle") return "action_in_progress";
            chef = actor; request = command; Inventory.Refresh(chef);
            selected = null; cargo = expected = originalSource = sourceStation = destination = null;
            sourceId = deliveryOperation = null; destinationId = command.Destination; delivered = 0; batchLimit = 1; spawnedChild = pendingStart = false; recovery.Reset(); plates.Clear();
            string error = Select(); if (error != null) return error;
            if (selected != null)
            {
                error = LogisticsChecks.Collect(chef, selected, command.Operation);
                if (error != null) return error;
            }
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
                if (command.Operation == "clear_station" && stack != null)
                {
                    var contents = ActionObservations.Read<IList>(stack, "m_stackItems");
                    for (int i = contents.Count - 1; i >= 0 && plates.Count < 64; i--) plates.Enqueue((GameObject)contents[i]);
                    batchLimit = plates.Count;
                }
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
                if (Inventory.DistanceTo(destination) == Single.MaxValue) return "destination_unreachable";
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
                    destination = ReturnDestination(chef, selected.Item);
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
                    if (!matches || LogisticsChecks.Collect(chef, candidate, request.Operation) != null) continue;
                    string delivery;
                    if (candidate.Source == destination || candidate.Item == destination ||
                        LogisticsChecks.Delivery(chef, request.Operation, LogisticsChecks.Payload(candidate), destination, out delivery, candidate.Supply) != null) continue;
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
        private GameObject ReturnDestination(PlayerControls actor, GameObject utensil)
        {
            GameObject home = Inventory.Home(utensil), best = null; float score = Single.MaxValue;
            foreach (GameObject station in navigation.Targets())
            {
                if (station == utensil || station.GetComponent<ServerRubbishBin>() != null) continue;
                var attach = station.GetComponent<ServerAttachStation>();
                if (attach == null) continue;
                float distance = Inventory.DistanceTo(station);
                if (distance == Single.MaxValue) continue;
                if (System.Object.ReferenceEquals(attach.InspectItem(), utensil)) return station;
                string delivery;
                if (LogisticsChecks.Delivery(actor, "return_utensil", utensil, station, out delivery) != null) continue;
                float preference = station == home ? 0 : station.GetComponent<CookingStation>() != null ? 1000 : 2000;
                if (preference + distance < score) { score = preference + distance; best = station; }
            }
            return best;
        }
        private bool AtDestination(GameObject item)
        { return item != null && destination != null && System.Object.ReferenceEquals(ActionObservations.Occupant(destination), item); }
        private string Delivery(GameObject item)
        {
            return LogisticsChecks.Delivery(chef, request.Operation, item, destination, out deliveryOperation, selected.Supply && item == selected.Item);
        }
        private string StartPickup()
        {
            Run.Advance("picking_up");
            string error = child.Start(chef, Run.Owner, new ControlCommand { Id = request.Id, Operation = "pick_up", Destination = sourceId });
            return ChildStarted(error);
        }
        private string StartDelivery()
        {
            Run.Advance("delivering");
            string error = Delivery(cargo);
            if (error == null)
            {
                error = child.Start(chef, Run.Owner, new ControlCommand { Id = request.Id, Operation = deliveryOperation, Destination = destinationId });
            }
            return ChildStarted(error);
        }
        private string ChildStarted(string error)
        {
            pendingStart = error != null; spawnedChild = error == null;
            if (error == null) { recovery.Clear(); return null; }
            input(0);
            if (recovery.Wait(error)) return null;
            Finish("blocked", error); return error;
        }
        public void Tick()
        {
            if (!Run.Running) return;
            try
            {
                if (Run.Expired(Time.realtimeSinceStartup)) { Finish("failed", "logistics_timeout"); return; }
                if (pendingStart)
                {
                    input(0);
                    if (Run.Phase == "delivering")
                    {
                        if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), cargo)) { Finish("blocked", "held_item_changed"); return; }
                        StartDelivery();
                    }
                    else
                    {
                        if (sourceStation != null && sourceStation.activeInHierarchy && sourceStation != originalSource &&
                            !System.Object.ReferenceEquals(ActionObservations.Occupant(sourceStation), originalSource) && !supply)
                        { Finish("blocked", "station_contents_changed"); return; }
                        var stack = originalSource == null ? null : originalSource.GetComponent<ServerStack>();
                        var spawner = sourceStation == null ? null : sourceStation.GetComponent<PickupItemSpawner>();
                        if ((stack != null && originalSource.GetComponent<CleanPlateStack>() != null && !System.Object.ReferenceEquals(stack.InspectTopOfStack(), expected)) ||
                            (supply && spawner != null && !System.Object.ReferenceEquals(spawner.m_itemPrefab, originalSource)))
                        { Finish("blocked", "station_contents_changed"); return; }
                        StartPickup();
                    }
                    return;
                }
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
                            string ingredientId, ingredientName;
                            matches = LogisticsInventory.IngredientIdentity(cargo, out ingredientId, out ingredientName) && ingredientId == selected.IngredientId;
                        }
                        else matches = selected.UtensilType != null && LogisticsInventory.UtensilType(cargo) == selected.UtensilType && LogisticsInventory.Empty(cargo);
                        if (!matches) { Finish("blocked", "supply_contents_changed"); return; }
                    }
                    StartDelivery(); return;
                }
                if (Run.Phase == "delivering")
                {
                    delivered++;
                    if (plates.Count > 0) plates.Dequeue();
                    if (request.Operation != "clear_station") { Finish("succeeded", "item_delivered"); return; }
                    if (sourceStation == null || !sourceStation.activeInHierarchy || ActionObservations.Occupant(sourceStation) == null) { Finish("succeeded", "station_cleared"); return; }
                    if (delivered >= batchLimit) { Finish("blocked", "station_repopulated"); return; }
                    if (!System.Object.ReferenceEquals(ActionObservations.Occupant(sourceStation), originalSource)) { Finish("blocked", "station_contents_changed"); return; }
                    var stack = originalSource == null ? null : originalSource.GetComponent<ServerStack>();
                    expected = plates.Count == 0 ? null : plates.Peek(); cargo = null;
                    if (stack == null || !System.Object.ReferenceEquals(stack.InspectTopOfStack(), expected)) { Finish("blocked", "station_contents_changed"); return; }
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
        private static string OptionSource(string operation, LogisticsItem entry)
        {
            switch (operation)
            {
                case "fetch_ingredient": return entry.IngredientId;
                case "get_plate": return entry.CleanPlate ? "" : null;
                case "get_utensil": return entry.Empty ? entry.UtensilType : null;
                case "return_utensil": return !entry.Supply && entry.Empty && entry.UtensilType != null ? entry.ItemId : null;
                case "clear_station": return !entry.Supply && entry.Source.GetComponent<ServerAttachStation>() != null ? entry.SourceId : null;
                default: return !entry.Supply ? entry.ItemId : null;
            }
        }
        public void Decorate(Json.Record catalog, PlayerControls actor)
        {
            var items = new List<object>(); var ingredients = new List<object>(); var utensils = new List<string>();
            var destinations = new List<object>(); var options = Json.Object();
            foreach (string operation in ActionRun.LogisticsNames) options[operation] = new List<object>();
            catalog["items"] = items; catalog["ingredients"] = ingredients; catalog["utensilTypes"] = utensils;
            catalog["deliveryDestinations"] = destinations; catalog["jobOptions"] = options;
            catalog["logisticsObservationErrors"] = Inventory.Errors;
            if (Run.Running || child.Run.Running || navigation.Action.State != "idle") return;
            Inventory.Refresh(actor, catalog);
            var reachable = new Dictionary<string, Json.Record>();
            foreach (Json.Record identity in (List<object>)catalog["destinations"]) reachable[(string)identity["id"]] = identity;
            var usedItems = new HashSet<string>(); var usedDestinations = new HashSet<string>();
            var ingredientIds = new HashSet<string>();
            var actions = (List<object>)catalog["actions"];
            foreach (string operation in ActionRun.LogisticsNames)
            {
                var rows = new Dictionary<string, Json.Record>();
                foreach (LogisticsItem entry in Inventory.Items)
                {
                    try
                    {
                        if (entry.Item == null || entry.Source == null) continue;
                        string source = OptionSource(operation, entry);
                        if (source == null || LogisticsChecks.Collect(actor, entry, operation) != null) continue;
                        var compatible = new List<string>();
                        GameObject home = operation == "return_utensil" ? ReturnDestination(actor, entry.Item) : null;
                        foreach (var pair in reachable)
                        {
                            GameObject station = navigation.Resolve(pair.Key);
                            if (station == entry.Source || station == entry.Item || (operation == "return_utensil" && station != home)) continue;
                            string delivery;
                            if (LogisticsChecks.Delivery(actor, operation, LogisticsChecks.Payload(entry), station, out delivery, entry.Supply) == null) compatible.Add(pair.Key);
                        }
                        if (compatible.Count == 0) continue;
                        Json.Record row;
                        if (!rows.TryGetValue(source, out row))
                        { row = Json.Object("sourceId", source, "destinationIds", new List<string>()); rows[source] = row; }
                        var ids = (List<string>)row["destinationIds"];
                        foreach (string id in compatible) { if (!ids.Contains(id)) ids.Add(id); usedDestinations.Add(id); }
                        usedItems.Add(entry.ItemId);
                        if (operation == "fetch_ingredient" && ingredientIds.Add(entry.IngredientId)) ingredients.Add(Json.Object("id", entry.IngredientId, "label", entry.IngredientName));
                        if (operation == "get_utensil" && !utensils.Contains(entry.UtensilType)) utensils.Add(entry.UtensilType);
                    }
                    catch (Exception) { Inventory.Errors.Add(Json.Object("sourceId", entry.SourceId, "reason", "logistics_option_unavailable")); }
                }
                var choices = new List<object>(); foreach (Json.Record row in rows.Values) choices.Add(row);
                options[operation] = choices;
                if (choices.Count == 0) continue;
                var parameters = Json.Object();
                var sources = new List<object>(); var deliveries = new List<object>(); var destinationIds = new HashSet<string>();
                foreach (Json.Record row in choices) foreach (string id in (List<string>)row["destinationIds"]) destinationIds.Add(id);
                foreach (string id in destinationIds) deliveries.Add(reachable[id]);
                if (operation == "move_item" || operation == "stage_item" || operation == "return_utensil")
                {
                    foreach (LogisticsItem entry in Inventory.Items) if (rows.ContainsKey(entry.ItemId)) sources.Add(entry.Describe());
                    parameters["item_id"] = Json.Object("type", "enum", "options", sources);
                }
                if (operation == "clear_station")
                {
                    foreach (string id in rows.Keys) sources.Add(navigation.TargetIdentity(navigation.Resolve(id)));
                    parameters["station_id"] = Json.Object("type", "enum", "options", sources);
                }
                if (operation == "fetch_ingredient") parameters["ingredient"] = Json.Object("type", "enum", "options", ingredients);
                if (operation == "get_utensil") parameters["utensil_type"] = Json.Object("type", "enum", "options", utensils);
                if (operation != "return_utensil") parameters["destination_id"] = Json.Object("type", "enum", "options", deliveries);
                actions.Add(Json.Object("name", operation, "parameters", parameters));
            }
            foreach (LogisticsItem entry in Inventory.Items) if (usedItems.Contains(entry.ItemId)) items.Add(entry.Describe());
            foreach (string id in usedDestinations) destinations.Add(reachable[id]);
        }
    }
}
