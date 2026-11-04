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
            if (args[0] == "in-flight")
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
                var bridges = Resources.FindObjectsOfTypeAll<BridgePlugin>();
                if (bridges.Length != 1) throw new InvalidOperationException("fixture_bridge_ambiguous");
                var control = Actions.ActionObservations.Read<Control.Player2Control>(bridges[0], "control");
                var task = Actions.ActionObservations.Read<Actions.TaskActions>(control, "tasks");
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
    }
}
