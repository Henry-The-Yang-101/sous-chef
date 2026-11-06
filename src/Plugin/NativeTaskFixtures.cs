using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal sealed partial class NativeSimulator
    {
        // Explicit checkpoints for native task audits. Never called by a
        // production action; measured chef execution uses logical inputs.
        private void TaskFixture(string arguments)
        {
            if (!Enabled || !KitchenGate.Inspect().InRound) throw new InvalidOperationException("task_fixture_requires_native_round");
            string[] args = arguments.Split(' ');
            if (args.Length < 2) throw new ArgumentException("task-fixture dirty COUNT [COUNTER_ID] | ignite OBJECT_ID | burnt ITEM_ID | in-flight ITEM_ID PLAYER");
            var result = Json.Object("fixture", args[0], "realtime", Time.realtimeSinceStartup, "generation", generation);
            if (args[0] == "ingredient" || args[0] == "loose-ingredient")
            {
                // Explicit supplied-food initial conditions for separated chefs.
                bool loose = args[0] == "loose-ingredient";
                int required = loose ? 5 : 3;
                bool chopped = args.Length == required + 1 && args[required] == "chopped";
                if (args.Length != required && !chopped) throw new ArgumentException("invalid_ingredient_fixture");
                bool definition = args[1].StartsWith("i", StringComparison.Ordinal);
                GameObject source = FixtureObject(Int32.Parse(definition ? args[2] : args[1]));
                var supply = source.GetComponent<PickupItemSpawner>();
                GameObject prefab = definition ? FixturePreparedIngredient(args[1]) : supply == null ? null : supply.m_itemPrefab;
                if (prefab == null || definition && loose) throw new ArgumentException("fixture_requires_configured_ingredient");
                GameObject target = loose ? source : FixtureObject(Int32.Parse(args[2]));
                var station = target.GetComponent<ServerAttachStation>();
                var carrier = target.GetComponent<ServerPlayerAttachmentCarrier>();
                if (!loose && (station == null && carrier == null || station != null && station.InspectItem() != null || carrier != null && carrier.InspectCarriedItem() != null))
                    throw new ArgumentException("fixture_requires_empty_holder");
                Vector3 point = Vector3.zero;
                if (loose)
                    for (int n = 0; n < 3; n++)
                    {
                        float value = Single.Parse(args[n + 2], System.Globalization.CultureInfo.InvariantCulture);
                        if (Single.IsNaN(value) || Single.IsInfinity(value) || Math.Abs(value) > 1000) throw new ArgumentException("invalid_fixture_position");
                        point[n] = value;
                    }
                if (chopped)
                {
                    var seen = new HashSet<GameObject>();
                    while (prefab != null && prefab.GetComponent<WorkableItem>() != null && seen.Add(prefab))
                        prefab = prefab.GetComponent<WorkableItem>().GetNextPrefab();
                    if (prefab == null || prefab.GetComponent<WorkableItem>() != null) throw new ArgumentException("fixture_requires_finished_work_output");
                }
                NetworkUtils.RegisterSpawnablePrefab(source, prefab);
                GameObject food = NetworkUtils.ServerSpawnPrefab(source, prefab);
                if (loose)
                {
                    food.transform.position = point;
                    // Complete the native detach path: a bare spawned prefab's
                    // body is initially kinematic until attached/detached.
                    food.GetComponent<ServerPhysicalAttachment>().Detach();
                }
                else if (station != null) station.AddItem(food, Vector2.up, new PlacementContext(PlacementContext.Source.Player));
                else carrier.CarryItem(food);
                result["itemInstanceId"] = food.GetInstanceID(); result["sourceInstanceId"] = source.GetInstanceID();
                result["preparation"] = chopped ? "chopped" : "raw";
                result["targetInstanceId"] = loose ? null : (object)target.GetInstanceID();
                if (definition) result["ingredientDefinitionId"] = args[1];
            }
            else if (args[0] == "serving-order")
            {
                // Explicit timed-ticket initial condition for expiry audits.
                if (args.Length != 3) throw new ArgumentException("serving-order PLATE_ID SECONDS");
                var plate = FixtureObject(Int32.Parse(args[1])).GetComponent<ServerPlate>();
                float seconds = Single.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
                if (plate == null || Single.IsNaN(seconds) || seconds < 1 || seconds > 300) throw new ArgumentException("invalid_serving_order_fixture");
                OrderDefinitionNode recipe = null;
                foreach (var client in UnityEngine.Object.FindObjectsOfType<ClientKitchenFlowControllerBase>())
                    foreach (var candidate in client.GetCachedRecipeList())
                        if (candidate.m_platingStep == plate.GetPlatingStep() && AssembledDefinitionNode.Matching(plate.GetOrderComposition(), candidate)) recipe = candidate;
                if (recipe == null) throw new InvalidOperationException("fixture_requires_known_plated_recipe");
                bool added = false;
                foreach (var station in UnityEngine.Object.FindObjectsOfType<ServerPlateStation>())
                {
                    foreach (var flow in UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>())
                    {
                        var monitor = flow.GetMonitorForTeam(station.GetTeamID());
                        if (monitor == null || monitor.OrdersController == null) continue;
                        var add = typeof(ServerOrderControllerBase).GetMethod("AddNewOrder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(RecipeList.Entry) }, null);
                        var order = (OrderController.ServerOrderData)add.Invoke(monitor.OrdersController,
                            new object[] { new RecipeList.Entry { m_order = recipe, m_scoreForMeal = 20 } });
                        order.Remaining = seconds; added = true; break;
                    }
                    if (added) break;
                }
                if (!added) throw new InvalidOperationException("fixture_requires_serving_orders");
                result["recipe"] = recipe.name; result["remainingSeconds"] = seconds;
            }
            else if (args[0] == "plate-food")
            {
                // Supplied plated-food initial condition for serving audits.
                // Use native ingredient definitions and content notifications;
                // measured pickup, clearing hands and serving still use inputs.
                if (args.Length != 3) throw new ArgumentException("plate-food PLATE_ID SUPPLY_ID");
                GameObject plate = FixtureObject(Int32.Parse(args[1]));
                var contents = plate.GetComponent<ServerIngredientContainer>();
                var supply = FixtureObject(Int32.Parse(args[2])).GetComponent<PickupItemSpawner>();
                if (plate.GetComponent<ServerPlate>() == null || contents == null || contents.GetContentsCount() != 0 || supply == null)
                    throw new ArgumentException("fixture_requires_empty_plate_and_supply");
                GameObject prefab = supply.m_itemPrefab;
                var seen = new HashSet<GameObject>();
                while (prefab != null && prefab.GetComponent<WorkableItem>() != null && seen.Add(prefab))
                    prefab = prefab.GetComponent<WorkableItem>().GetNextPrefab();
                var composition = Actions.ActionObservations.Composition(prefab);
                if (composition == null || !contents.CanAddIngredient(composition)) throw new ArgumentException("fixture_requires_platable_ingredient");
                contents.AddIngredient(composition);
                result["plateInstanceId"] = plate.GetInstanceID(); result["ingredient"] = prefab.name;
            }
            else if (args[0] == "stance")
            {
                // Exercise the production throw-approach navigator without
                // replacing its route, collision or support predicates.
                if (args.Length != 5) throw new ArgumentException("invalid_stance_fixture");
                fixtureStanceAim = FixtureObject(Int32.Parse(args[1]));
                for (int n = 0; n < 3; n++)
                {
                    float value = Single.Parse(args[n + 2], System.Globalization.CultureInfo.InvariantCulture);
                    if (Single.IsNaN(value) || Single.IsInfinity(value) || Math.Abs(value) > 1000) throw new ArgumentException("invalid_fixture_position");
                    fixtureStancePoint[n] = value;
                }
                result["point"] = Values.Vector(fixtureStancePoint);
                result["destination"] = "d0000";
            }
            else if (args[0] == "collect")
            {
                // External human pickup checkpoint, never a P2 task mutation.
                if (args.Length != 3) throw new ArgumentException("invalid_collection_fixture");
                GameObject food = FixtureObject(Int32.Parse(args[1])), target = FixtureObject(Int32.Parse(args[2]));
                var player = target.GetComponent<PlayerControls>(); var carrier = target.GetComponent<ServerPlayerAttachmentCarrier>();
                var attachment = food.GetComponent<ServerPhysicalAttachment>();
                Vector3 delta = food.transform.position - target.transform.position; delta.y = 0;
                if (player == null || player.PlayerIDProvider == null || player.PlayerIDProvider.GetID() == PlayerInputLookup.Player.Two ||
                    carrier == null || carrier.InspectCarriedItem() != null || attachment == null || attachment.IsAttached() || delta.magnitude > 1.6f)
                    throw new ArgumentException("fixture_requires_obtainable_loose_food_and_empty_human_hands");
                carrier.CarryItem(food); result["itemInstanceId"] = food.GetInstanceID(); result["playerInstanceId"] = target.GetInstanceID();
            }
            else if (args[0] == "borrow-cookware")
            {
                // Initial single-pan checkpoint: P1 keeps the spare empty pan
                // in hand, so only one pan can be used throughout the trial.
                if (args.Length != 3) throw new ArgumentException("borrow-cookware STATION_ID HUMAN_ID");
                var station = FixtureObject(Int32.Parse(args[1])).GetComponent<ServerAttachStation>();
                var human = FixtureObject(Int32.Parse(args[2])).GetComponent<PlayerControls>();
                var carrier = human == null ? null : human.GetComponent<ServerPlayerAttachmentCarrier>();
                var pan = station == null ? null : station.InspectItem();
                if (carrier == null || human.PlayerIDProvider.GetID() != PlayerInputLookup.Player.One || carrier.InspectCarriedItem() != null ||
                    pan == null || pan.GetComponent<ServerCookingHandler>() == null || !Actions.LogisticsInventory.Empty(pan) || !station.CanHandlePickup(carrier))
                    throw new ArgumentException("fixture_requires_empty_cookware_and_human_hands");
                station.HandlePickup(carrier, Vector2.up);
                if (carrier.InspectCarriedItem() != pan || station.InspectItem() != null) throw new InvalidOperationException("cookware_not_borrowed");
                result["itemInstanceId"] = pan.GetInstanceID();
            }
            else if (args[0] == "wear-pack")
            {
                if (args.Length != 3) throw new ArgumentException("wear-pack PACK_ID HUMAN_ID");
                var pack = FixtureObject(Int32.Parse(args[1])).GetComponent<ServerBackpack>();
                var human = FixtureObject(Int32.Parse(args[2])).GetComponent<PlayerControls>();
                if (pack == null || human == null || human.PlayerIDProvider.GetID() != PlayerInputLookup.Player.One)
                    throw new InvalidOperationException("fixture_requires_backpack_and_human");
                var carrier = human.GetComponent<ServerPlayerAttachmentCarrier>();
                if (carrier.HasAttachment(pack.PlayerAttachTarget)) throw new InvalidOperationException("back_slot_occupied");
                pack.HandlePickup(carrier, Vector2.up);
                if (carrier.InspectCarriedItem(pack.PlayerAttachTarget) != pack.gameObject)
                    throw new InvalidOperationException("backpack_not_equipped");
                result["pack"] = pack.gameObject.GetInstanceID(); result["human"] = human.gameObject.GetInstanceID();
            }
            else if (args[0] == "lose-held")
            {
                // Reproduce a despawn during the walking portion of a handoff;
                // never allow this fixture to remove unrelated or replacement cargo.
                if (args.Length != 2) throw new ArgumentException("lose-held ITEM_ID");
                var item = FixtureObject(Int32.Parse(args[1]));
                var task = FixtureTask();
                var chef = Actions.ActionObservations.Read<PlayerControls>(task, "chef");
                if (!task.Run.Running || task.Run.Phase != "approaching" || Actions.ActionObservations.Held(chef) != item)
                    throw new InvalidOperationException("fixture_requires_active_walk_with_exact_cargo");
                chef.GetComponent<ServerPlayerAttachmentCarrier>().DestroyCarriedItem();
                result["lost"] = Int32.Parse(args[1]); result["requestId"] = task.Run.RequestId;
            }
            else if (args[0] == "sources")
            {
                var task = FixtureTask();
                var inventory = Actions.ActionObservations.Read<Actions.LogisticsInventory>(task, "inventory");
                var actor = Actions.ActionObservations.Read<PlayerControls>(task, "chef");
                var nav = Actions.ActionObservations.Read<Navigation.NavigationActions>(task, "navigation");
                var request = Actions.ActionObservations.Read<TaskRequest>(task, "request");
                var sources = new List<object>(); result["complete"] = inventory.Catalog["complete"];
                foreach (var entry in inventory.Items)
                {
                    var row = (Json.Record)entry.Describe(); var matches = new List<bool>();
                    foreach (var desired in request.Items) matches.Add(Actions.LogisticsInventory.IngredientMatches(entry, desired.Selector));
                    row["matches"] = matches; row["collect"] = Actions.LogisticsChecks.Collect(actor, entry, "move_item");
                    row["sameSupport"] = nav.SharesCurrentSupport(entry.Source); sources.Add(row);
                }
                result["sources"] = sources;
            }
            else if (args[0] == "in-flight")
            {
                // Seed the post-dispatch checkpoint for scheduler integration
                // tests. Cargo must already be travelling on a real native belt;
                // this fixture never moves it or performs its placement.
                if (args.Length != 3) throw new ArgumentException("invalid_inflight_fixture");
                GameObject item = FixtureObject(Int32.Parse(args[1])); int player = Int32.Parse(args[2]);
                if (player != 1 && player != 3 && player != 4) throw new ArgumentException("invalid_delivery_player");
                bool onBelt = false;
                foreach (var station in UnityEngine.Object.FindObjectsOfType<ServerAttachStation>())
                    if (station.GetComponent<ServerConveyorStation>() != null && System.Object.ReferenceEquals(station.InspectItem(), item)) onBelt = true;
                if (!onBelt) throw new InvalidOperationException("fixture_requires_native_belt_cargo");
                var task = FixtureTask();
                var request = Actions.ActionObservations.Read<TaskRequest>(task, "request");
                if (!task.Run.Running || request.Action != "prepare_near_player") throw new InvalidOperationException("fixture_requires_running_preparation");
                task.DispatchDelivery(item, player); result["requestId"] = task.Run.RequestId; result["itemInstanceId"] = item.GetInstanceID();
            }
            else if (args[0] == "dirty")
            {
                int count = Int32.Parse(args[1]); if (count < 1 || count > 10 || args.Length > 3) throw new ArgumentException("invalid_dirty_fixture");
                ServerPlateReturnStation station = null;
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<ServerPlateReturnStation>())
                {
                    var definition = Actions.ActionObservations.Read<PlateReturnStation>(candidate, "m_returnStation");
                    if (definition.m_stackPrefab != null && definition.m_stackPrefab.GetComponent<DirtyPlateStack>() != null) { station = candidate; break; }
                }
                if (station == null) throw new InvalidOperationException("no_native_dirty_return_station");
                GameObject stack;
                if (args.Length == 2)
                {
                    for (int n = 0; n < count; n++) station.ReturnPlate();
                    stack = Actions.ActionObservations.Occupant(station.gameObject);
                }
                else
                {
                    GameObject target = FixtureObject(Int32.Parse(args[2]));
                    var attach = target.GetComponent<ServerAttachStation>();
                    if (attach == null || attach.InspectItem() != null) throw new InvalidOperationException("dirty_fixture_requires_empty_counter");
                    PlateReturnStation definition = Actions.ActionObservations.Read<PlateReturnStation>(station, "m_returnStation");
                    NetworkUtils.RegisterSpawnablePrefab(target, definition.m_stackPrefab);
                    stack = NetworkUtils.ServerSpawnPrefab(target, definition.m_stackPrefab);
                    var dirty = stack.GetComponent<ServerDirtyPlateStack>();
                    if (dirty == null) throw new InvalidOperationException("native_dirty_stack_missing");
                    for (int n = 0; n < count; n++) dirty.AddToStack();
                    attach.AddItem(stack, Vector2.up, new PlacementContext(PlacementContext.Source.Player));
                    result["counterInstanceId"] = target.GetInstanceID();
                }
                result["stackInstanceId"] = stack == null ? null : (object)stack.GetInstanceID(); result["count"] = count;
            }
            else
            {
                if (args.Length != 2) throw new ArgumentException("invalid_task_fixture");
                GameObject target = FixtureObject(Int32.Parse(args[1])); result["objectInstanceId"] = target.GetInstanceID();
                if (args[0] == "ignite")
                {
                    var flammable = target.GetComponent<ServerFlammable>();
                    if (flammable == null) throw new InvalidOperationException("fixture_target_not_flammable");
                    flammable.Ignite();
                }
                else if (args[0] == "burnt")
                {
                    var cooking = target.GetComponent<ServerCookingHandler>();
                    var contents = target.GetComponent<ServerIngredientContainer>();
                    if (cooking == null || contents == null || contents.GetContentsCount() == 0) throw new InvalidOperationException("fixture_requires_real_food_in_cooker");
                    cooking.SetCookingProgress(cooking.AccessCookingTime * 2 + 1);
                }
                else throw new ArgumentException("unknown_task_fixture");
            }
            Write("task-fixture.json", result);
        }
        private static GameObject FixturePreparedIngredient(string id)
        {
            // Resolve only native food configured in this kitchen, including
            // automatic/conveyor sources. This is supplied initial stock, not
            // a production replacement for a lost allocation.
            var sources = new List<GameObject>();
            foreach (var supply in UnityEngine.Object.FindObjectsOfType<PickupItemSpawner>()) sources.Add(supply.gameObject);
            foreach (var switcher in UnityEngine.Object.FindObjectsOfType<PickupItemSwitcher>()) sources.Add(switcher.gameObject);
            foreach (var root in new Actions.PreparationIngredients().Observe(sources))
            {
                GameObject prefab = root.Prefab; var seen = new HashSet<GameObject>();
                while (prefab != null && prefab.GetComponent<WorkableItem>() != null && seen.Add(prefab))
                    prefab = prefab.GetComponent<WorkableItem>().GetNextPrefab();
                string ingredient, name;
                if (prefab != null && Actions.LogisticsInventory.IngredientIdentity(prefab, out ingredient, out name) && ingredient == id) return prefab;
            }
            return null;
        }
        private static GameObject FixtureObject(int id)
        {
            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>()) if (t.gameObject.GetInstanceID() == id) return t.gameObject;
            throw new ArgumentException("fixture_object_missing");
        }
        private static Actions.TaskActions FixtureTask()
        {
            var bridges = Resources.FindObjectsOfTypeAll<BridgePlugin>();
            if (bridges.Length != 1) throw new InvalidOperationException("fixture_bridge_ambiguous");
            var control = Actions.ActionObservations.Read<Control.Player2Control>(bridges[0], "control");
            return Actions.ActionObservations.Read<Actions.TaskActions>(control, "tasks");
        }
    }
}
