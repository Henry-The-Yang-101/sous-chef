using System;
using System.Collections.Generic;
using SousChef.Core;
using SousChef.Plugin.Adapters;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SousChef.Plugin
{
    // Unity references stay on the main thread. Only detached value records are published.
    // Full captures reconcile the registry; native spawns and reference closure are checked each tick.
    internal sealed class GameplayCapture
    {
        private sealed class Entry
        {
            public GameObject Object;
            public Component[] Components;
        }
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly Queue<GameObject> pending = new Queue<GameObject>();
        private readonly HashSet<int> visited = new HashSet<int>();
        private readonly FieldReader reader;
        private readonly Func<GameObject, string> identify;
        private readonly string session;
        private float reconciledAt;
        private long baseline;
        public GameplayCapture(string sessionId, Func<GameObject, string> id)
        {
            session = sessionId; identify = id; reader = new FieldReader(Reference);
        }
        public void Reset() { entries.Clear(); pending.Clear(); baseline = 0; }
        public void BeginReconciliation() { entries.Clear(); }
        public void Register(GameObject go, Component[] components)
        {
            var selected = new List<Component>();
            foreach (Component c in components)
                if (c != null && (c is Collider || c is Rigidbody || reader.Supports(c.GetType()))) selected.Add(c);
            if (selected.Count > 0) entries[go.GetInstanceID()] = new Entry { Object = go, Components = selected.ToArray() };
            else entries.Remove(go.GetInstanceID());
        }
        public void Reconciled(long sequence) { baseline = sequence; reconciledAt = Time.realtimeSinceStartup; }
        private void Enqueue(GameObject go)
        {
            if (go != null && go.scene.IsValid()) pending.Enqueue(go);
        }
        private object Reference(Object value)
        {
            if (value == null) return null;
            Component c = value as Component;
            GameObject go = c == null ? value as GameObject : c.gameObject;
            if (go != null && go.scene.IsValid())
            {
                Enqueue(go);
                return Json.Object("objectId", identify(go), "component", c == null ? null : c.GetType().FullName,
                    "componentInstanceId", c == null ? null : (object)c.GetInstanceID());
            }
            return Json.Object("kind", "asset_reference", "name", value.name, "type", value.GetType().FullName,
                "assetInstanceId", value.GetInstanceID());
        }
        public CapturePacket Capture(KitchenGate gate, int epoch, long sequence, long ticks, long coalesced)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            float started = Time.realtimeSinceStartup;
            pending.Clear(); visited.Clear(); reader.BeginFrame();
            var dead = new List<int>();
            foreach (var pair in entries)
                if (pair.Value.Object == null) dead.Add(pair.Key); else Enqueue(pair.Value.Object);
            foreach (int key in dead) entries.Remove(key);
            foreach (var pair in EntitySerialisationRegistry.m_EntitiesByGameObject) Enqueue(pair.Key);
            Enqueue(gate.Flow.gameObject);
            var objects = new List<object>(); var errors = new List<object>();
            while (pending.Count > 0)
            {
                GameObject go = pending.Dequeue();
                if (go == null || !visited.Add(go.GetInstanceID())) continue;
                try
                {
                    Entry entry;
                    if (!entries.TryGetValue(go.GetInstanceID(), out entry) || entry.Object != go)
                    {
                        Register(go, go.GetComponents<Component>());
                        if (!entries.TryGetValue(go.GetInstanceID(), out entry))
                            entry = new Entry { Object = go, Components = new Component[0] };
                    }
                    var components = new List<object>(); var colliders = new List<object>();
                    Transform t = go.transform;
                    var record = Json.Object("id", identify(go), "name", go.name, "activeInHierarchy", go.activeInHierarchy,
                        "layer", go.layer, "transform", Values.Transform(t), "parentId", t.parent == null ? null : identify(t.parent.gameObject),
                        "components", components, "colliders", colliders);
                    foreach (Component c in entry.Components)
                    {
                        if (c == null) continue;
                        if (reader.Supports(c.GetType()))
                        {
                            var observation = Json.Object("type", c.GetType().FullName, "instanceId", c.GetInstanceID(), "adapters", reader.ReadComponent(c));
                            Behaviour b = c as Behaviour;
                            if (b != null) observation["enabled"] = b.enabled;
                            components.Add(observation);
                        }
                        Rigidbody body = c as Rigidbody;
                        if (body != null) record["rigidbody"] = Json.Object("position", Values.Vector(body.position),
                            "rotation", Values.Rotation(body.rotation), "velocity", Values.Vector(body.velocity),
                            "angularVelocity", Values.Vector(body.angularVelocity), "isKinematic", body.isKinematic);
                        Collider collider = c as Collider;
                        if (collider != null) colliders.Add(Json.Object("componentInstanceId", c.GetInstanceID(),
                            "enabled", collider.enabled, "isTrigger", collider.isTrigger, "worldBounds", Values.Bounds(collider.bounds)));
                    }
                    objects.Add(record);
                }
                catch (Exception e) { errors.Add(Json.Object("objectId", identify(go), "error", e.GetType().Name)); }
            }
            watch.Stop();
            return new CapturePacket { Sequence = sequence, Epoch = epoch, Frame = Json.Object(
                "type", "gameplay", "protocolVersion", 1, "sessionId", session, "levelEpoch", epoch,
                "sequence", sequence, "baselineSequence", baseline, "inRound", true, "role", gate.Role,
                "paused", TimeManager.IsPaused(TimeManager.PauseLayer.Main), "objects", objects,
                "clock", Json.Object("realtime", started, "renderFrame", Time.frameCount, "fixedUpdateCount", ticks,
                    "gameTime", Time.time, "captureStage", "WaitForEndOfFrame"),
                "coverage", Json.Object("semantics", "complete_gameplay_sample", "discoveryAgeSeconds", started - reconciledAt,
                    "errors", errors, "fieldErrors", reader.Errors, "missingFields", new List<string>(reader.MissingFields),
                    "truncatedValues", reader.TruncatedValues,
                    "limitations", new[] { "not a full map; collider bounds only", "non-native spawns and component changes discovered at full capture rate",
                        "animation state and mesh geometry remain in full snapshots", "sampled events only" }),
                "diagnostics", Json.Object("captureMilliseconds", watch.Elapsed.TotalMilliseconds, "coalescedCaptures", coalesced)) };
        }
    }
}
