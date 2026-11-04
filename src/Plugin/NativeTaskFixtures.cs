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
                GameObject source = FixtureObject(Int32.Parse(args[1]));
                var supply = source.GetComponent<PickupItemSpawner>();
                if (supply == null || supply.m_itemPrefab == null) throw new ArgumentException("fixture_requires_ingredient_supply");
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
                GameObject prefab = supply.m_itemPrefab;
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
