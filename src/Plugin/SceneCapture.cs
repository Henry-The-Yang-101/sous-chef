using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Adapters;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SousChef.Plugin
{
    internal sealed class SceneCapture
    {
        private readonly IdentityBook identities;
        public readonly GameplayCapture Gameplay;
        private readonly FieldReader reader;
        private readonly GeometryCapture geometry;
        private readonly HashSet<int> seen = new HashSet<int>();
        private readonly HashSet<int> included = new HashSet<int>();
        private readonly Queue<GameObject> referenced = new Queue<GameObject>();
        private Dictionary<string, string> previousParents = new Dictionary<string, string>();
        private readonly string session;
        private string flowIdentity;
        public int Epoch { get { return identities.Epoch; } }

        public SceneCapture(string sessionId)
        {
            session = sessionId; identities = new IdentityBook(sessionId);
            Gameplay = new GameplayCapture(sessionId, Id);
            reader = new FieldReader(Reference); geometry = new GeometryCapture(Reference);
        }

        public bool Matches(KitchenGate gate)
        {
            return flowIdentity == gate.Flow.gameObject.scene.GetHashCode() + ":" + gate.Flow.GetInstanceID();
        }

        public void EndRound()
        {
            flowIdentity = null; previousParents.Clear(); geometry.Reset(); Gameplay.Reset();
        }

        public CapturePacket Capture(KitchenGate gate, long sequence, long fixedTicks, long coalesced)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string key = gate.Flow.gameObject.scene.GetHashCode() + ":" + gate.Flow.GetInstanceID();
            if (flowIdentity != key)
            {
                identities.NewEpoch(); geometry.Reset(); Gameplay.Reset(); previousParents.Clear(); flowIdentity = key;
            }
            Gameplay.BeginReconciliation();
            seen.Clear(); included.Clear(); referenced.Clear(); reader.BeginFrame(); geometry.BeginFrame();
            var objects = new List<object>(); var scenes = new List<object>();
            var inventory = new Dictionary<string, int>(); var unsupported = new Dictionary<string, int>();
            var errors = new List<object>(); var parents = new Dictionary<string, string>();
            int missingScripts = 0;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                scenes.Add(Json.Object("handle", scene.GetHashCode(), "name", scene.name, "path", scene.path));
                foreach (GameObject root in scene.GetRootGameObjects()) EnqueueTree(root.transform);
            }
            // Add referenced persistent managers/attachments, without sweeping
            // account/session/save managers in DontDestroyOnLoad.
            referenced.Enqueue(gate.Flow.gameObject);
            foreach (var pair in EntitySerialisationRegistry.m_EntitiesByGameObject)
                if (pair.Key != null && pair.Key.scene.IsValid()) referenced.Enqueue(pair.Key);

            while (referenced.Count > 0)
            {
                GameObject go = referenced.Dequeue();
                if (go == null || !go.scene.IsValid() || !included.Add(go.GetInstanceID())) continue;
                try
                {
                    string id = Id(go); Transform t = go.transform;
                    string parent = t.parent == null ? null : Id(t.parent.gameObject);
                    if (t.parent != null) referenced.Enqueue(t.parent.gameObject);
                    parents[id] = parent;
                    uint native = EntitySerialisationRegistry.GetId(go);
                    var record = Json.Object("id", id, "unityInstanceId", go.GetInstanceID(), "nativeEntityId", native == 0 ? null : (object)native,
                        "name", go.name, "sceneHandle", go.scene.GetHashCode(), "parentId", parent, "activeSelf", go.activeSelf,
                        "activeInHierarchy", go.activeInHierarchy, "layer", go.layer, "tag", go.tag, "isStatic", go.isStatic,
                        "transform", Values.Transform(t));
                    var components = new List<object>(); var colliders = new List<object>();
                    Component[] discovered = go.GetComponents<Component>();
                    Gameplay.Register(go, discovered);
                    foreach (Component component in discovered)
                    {
                        if (component == null) { missingScripts++; continue; }
                        string type = component.GetType().FullName; Increment(inventory, type);
                        var observation = Json.Object("type", type, "instanceId", component.GetInstanceID(),
                            "authority", component.GetType().Name.StartsWith("Server", StringComparison.Ordinal) ? "server" : component.GetType().Name.StartsWith("Client", StringComparison.Ordinal) ? "client" : "shared");
                        Behaviour behaviour = component as Behaviour;
                        if (behaviour != null) observation["enabled"] = behaviour.enabled;
                        try
                        {
                            if (reader.Supports(component.GetType())) observation["adapters"] = reader.ReadComponent(component);
                            else if (!(component is Transform) && !(component is Collider) && !(component is Rigidbody) && !(component is Animator))
                            {
                                observation["status"] = "inventory_only";
                                if (!type.StartsWith("UnityEngine.", StringComparison.Ordinal)) Increment(unsupported, type);
                            }
                            var collider = component as Collider;
                            if (collider != null) colliders.Add(geometry.Collider(collider));
                            var body = component as Rigidbody;
                            if (body != null) record["rigidbody"] = geometry.Body(body);
                            var animator = component as Animator;
                            if (animator != null)
                            {
                                observation["animation"] = geometry.Animation(animator);
                                var behaviours = new List<object>();
                                foreach (var state in animator.GetBehaviours<StateMachineBehaviour>())
                                {
                                    if (state == null) continue;
                                    if (reader.Supports(state.GetType())) behaviours.Add(reader.Read(state));
                                    else Increment(unsupported, state.GetType().FullName);
                                }
                                observation["stateMachineBehaviours"] = behaviours;
                            }
                            var wind = component as WindVolume;
                            if (wind != null) observation["velocity"] = Values.Vector(wind.GetVelocity());
                            var serverFlow = component as ServerKitchenFlowControllerBase;
                            if (serverFlow != null && serverFlow.RoundTimer != null) observation["timerSuppressed"] = serverFlow.RoundTimer.IsSuppressed;
                            var clientFlow = component as ClientKitchenFlowControllerBase;
                            if (clientFlow != null && clientFlow.RoundTimer != null) observation["timerSuppressed"] = clientFlow.RoundTimer.IsSuppressed;
                        }
                        catch (Exception e)
                        {
                            observation["status"] = "read_error"; observation["error"] = e.GetType().Name;
                            errors.Add(Json.Object("objectId", id, "component", type, "error", e.Message));
                        }
                        components.Add(observation);
                    }
                    record["components"] = components; record["colliders"] = colliders; objects.Add(record);
                }
                catch (Exception e) { errors.Add(Json.Object("object", go.name, "error", e.GetType().Name)); }
            }

            var created = new List<string>(); var removed = new List<string>(); var reparented = new List<object>();
            foreach (var p in parents)
            {
                string old;
                if (!previousParents.TryGetValue(p.Key, out old)) created.Add(p.Key);
                else if (p.Value != old) reparented.Add(Json.Object("id", p.Key, "from", old, "to", p.Value));
            }
            foreach (string id in previousParents.Keys) if (!parents.ContainsKey(id)) removed.Add(id);
            previousParents = parents; identities.Retain(seen);
            geometry.EndFrame();
            object physics = GeometryCapture.PhysicsSettings();
            Gameplay.Reconciled(sequence); watch.Stop();
            var snapshot = Json.Object("type", "snapshot", "protocolVersion", 1, "sessionId", session,
                "sequence", sequence, "levelEpoch", Epoch, "role", gate.Role, "inRound", true,
                "flowId", Id(gate.Flow.gameObject), "scenes", scenes, "paused", TimeManager.IsPaused(TimeManager.PauseLayer.Main),
                "clock", Json.Object("renderFrame", Time.frameCount, "fixedUpdateCount", fixedTicks,
                    "gameTime", Time.time, "fixedTime", Time.fixedTime, "realtime", Time.realtimeSinceStartup,
                    "timeScale", Time.timeScale, "deltaTime", Time.deltaTime, "fixedDeltaTime", Time.fixedDeltaTime,
                    "captureStage", "WaitForEndOfFrame"),
                "geometryRevision", sequence, "meshRevision", geometry.Revision, "physics", physics, "objects", objects,
                "changes", Json.Object("semantics", "sampled_diff", "created", created, "removed", removed, "reparented", reparented),
                "coverage", Json.Object("usedAdapters", new List<string>(reader.UsedAdapters),
                    "componentInventory", CountObject(inventory), "unsupportedComponents", CountObject(unsupported),
                    "unsupportedValues", CountObject(reader.UnsupportedValues), "missingFields", new List<string>(reader.MissingFields),
                    "missingScripts", missingScripts, "truncatedValues", reader.TruncatedValues, "fieldErrors", reader.Errors,
                    "errors", errors, "limitations", new[] { "sampled events only", "per-collider collision ignore pairs unavailable in Unity 2017.4",
                        "unreadable collision meshes reported", "coroutine locals and RNG unobserved",
                        "mesh refresh interval 2 seconds", "client role lacks authoritative server state" }),
                "diagnostics", Json.Object("captureMilliseconds", watch.Elapsed.TotalMilliseconds, "coalescedCaptures", coalesced));
            return new CapturePacket { Sequence = sequence, Epoch = Epoch, Frame = snapshot,
                Assets = new Dictionary<string, object>(geometry.Assets) };
        }

        private void EnqueueTree(Transform transform)
        {
            // UI has no kitchen collision/semantics and can contain huge unrelated
            // menu hierarchies. Any actual reference to it is still followed below.
            if (transform is RectTransform) return;
            referenced.Enqueue(transform.gameObject);
            for (int i = 0; i < transform.childCount; i++) EnqueueTree(transform.GetChild(i));
        }

        private string Id(GameObject go)
        {
            int key = go.GetInstanceID(); seen.Add(key); return identities.Get(key, go);
        }

        private object Reference(Object value)
        {
            if (value == null) return null;
            var component = value as Component; var go = value as GameObject;
            if (component != null) go = component.gameObject;
            if (go != null && go.scene.IsValid())
            {
                referenced.Enqueue(go);
                return Json.Object("objectId", Id(go), "component", component == null ? null : component.GetType().FullName,
                    "componentInstanceId", component == null ? null : (object)component.GetInstanceID());
            }
            return Json.Object("kind", "asset_reference", "name", value.name, "type", value.GetType().FullName,
                "assetInstanceId", value.GetInstanceID());
        }

        private static void Increment(Dictionary<string, int> map, string key)
        {
            int count; map.TryGetValue(key, out count); map[key] = count + 1;
        }
        private static object CountObject(Dictionary<string, int> counts)
        {
            var result = new Dictionary<string, object>(); foreach (var p in counts) result[p.Key] = p.Value; return result;
        }
    }
}
