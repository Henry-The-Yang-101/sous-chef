using System.Collections.Generic;
using System.Diagnostics;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal sealed partial class SceneCapture
    {
        private readonly List<Component> viewComponents = new List<Component>();
        private static readonly object[] empty = new object[0];

        // Only these adapter families supply relationships read by ContentsIndex.
        private static bool SuppliesContents(Component c)
        {
            return c is ServerAttachStation || c is ClientAttachStation ||
                c is ServerWorkstation || c is ClientWorkstation || c is ServerCookingStation ||
                c is ServerPlateReturnStation || c is ServerPlateStackBase || c is ClientPlateStackBase ||
                c is ServerStack || c is ClientStack || c is ServerIngredientContainer ||
                c is ClientIngredientContainer || c is ServerWashingStation;
        }

        public CapturePacket CaptureHarness(KitchenGate gate, long sequence)
        {
            var watch = Stopwatch.StartNew();
            BeginObservation(gate, null);
            var objects = new List<object>();
            var layers = new List<object>();
            for (int i = 0; i < 32; i++)
                layers.Add(Json.Object("index", i, "name", LayerMask.LayerToName(i)));

            while (referenced.Count > 0)
            {
                GameObject go = referenced.Dequeue();
                if (go == null || !go.activeInHierarchy || !go.scene.IsValid() || !included.Add(go.GetInstanceID())) continue;
                List<object> holders = null, colliders = null;
                bool player = false;
                go.GetComponents(viewComponents);
                foreach (Component component in viewComponents)
                {
                    if (component == null) continue;
                    var collider = component as Collider;
                    if (collider != null && collider.enabled && !collider.isTrigger)
                    {
                        if (colliders == null) colliders = new List<object>();
                        colliders.Add(geometry.Collider(collider));
                    }
                    else if (component is PlayerControls || SuppliesContents(component))
                    {
                        if (holders == null) holders = new List<object>();
                        var record = Json.Object("type", component.GetType().FullName);
                        if (component is PlayerControls) player = true;
                        else record["adapters"] = reader.ReadComponent(component);
                        holders.Add(record);
                    }
                }
                var obj = Json.Object("id", Id(go), "unityInstanceId", go.GetInstanceID(), "name", go.name,
                    "layer", go.layer, "components", (object)holders ?? empty, "colliders", (object)colliders ?? empty);
                if (player) obj["transform"] = Json.Object("position", Values.Vector(go.transform.position));
                else if (colliders != null) obj["transform"] = Json.Object("localToWorld", Values.Matrix(go.transform.localToWorldMatrix));
                objects.Add(obj);
            }
            viewComponents.Clear();
            identities.Retain(seen);
            geometry.EndFrame();
            var frame = Json.Object("type", "harness", "protocolVersion", 1, "sessionId", session, "sequence", sequence,
                "levelEpoch", Epoch, "inRound", true, "role", gate.Role, "objects", objects, "physics", Json.Object("layers", layers),
                "clock", Json.Object("renderFrame", Time.frameCount, "realtime", Time.realtimeSinceStartup),
                "diagnostics", Json.Object("captureMilliseconds", watch.Elapsed.TotalMilliseconds));
            return new CapturePacket { Sequence = sequence, Epoch = Epoch, Frame = frame,
                Assets = new Dictionary<string, object>(geometry.Assets) };
        }
    }
}
