using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal sealed partial class NativeSimulator
    {
        // Explicit initial conditions for native task audits. Never called by a
        // production action; all subsequent task execution uses logical inputs.
        private void TaskFixture(string arguments)
        {
            if (!Enabled || !KitchenGate.Inspect().InRound) throw new InvalidOperationException("task_fixture_requires_native_round");
            string[] args = arguments.Split(' ');
            if (args.Length < 2) throw new ArgumentException("task-fixture dirty COUNT [COUNTER_ID] | ignite OBJECT_ID | burnt ITEM_ID");
            var result = Json.Object("fixture", args[0], "realtime", Time.realtimeSinceStartup, "generation", generation);
            if (args[0] == "dirty")
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
