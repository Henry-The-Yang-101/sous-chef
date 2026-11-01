using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SousChef.Plugin
{
    internal sealed class GeometryCapture
    {
        private sealed class MeshEntry
        {
            public Mesh Mesh;
            public string Id;
            public uint Fingerprint;
            public float RefreshedAt;
            public object Asset;
        }
        private sealed class TerrainEntry
        {
            public TerrainData Terrain;
            public string Id;
            public float RefreshedAt;
            public uint Fingerprint;
            public object Asset;
        }
        private readonly Dictionary<int, MeshEntry> meshes = new Dictionary<int, MeshEntry>();
        private readonly Dictionary<int, TerrainEntry> terrains = new Dictionary<int, TerrainEntry>();
        private readonly Func<Object, object> reference;
        private int nextAsset;
        public int Revision { get; private set; }
        public readonly Dictionary<string, object> Assets = new Dictionary<string, object>();
        public GeometryCapture(Func<Object, object> objectReference) { reference = objectReference; }

        public void Reset() { meshes.Clear(); terrains.Clear(); Assets.Clear(); Revision++; }
        public void BeginFrame() { Assets.Clear(); }

        public void EndFrame()
        {
            var removed = new List<int>();
            foreach (var pair in meshes)
                if (pair.Value.Id == null || !Assets.ContainsKey(pair.Value.Id)) removed.Add(pair.Key);
            foreach (int key in removed) meshes.Remove(key);
            removed.Clear();
            foreach (var pair in terrains)
                if (pair.Value.Id == null || !Assets.ContainsKey(pair.Value.Id)) removed.Add(pair.Key);
            foreach (int key in removed) terrains.Remove(key);
        }

        public object Collider(Collider collider)
        {
            var result = Json.Object("componentInstanceId", collider.GetInstanceID(), "type", collider.GetType().Name,
                "enabled", collider.enabled, "isTrigger", collider.isTrigger, "contactOffset", collider.contactOffset,
                "worldBounds", Values.Bounds(collider.bounds), "attachedBody", reference(collider.attachedRigidbody));
            PhysicMaterial material = collider.sharedMaterial;
            if (material != null) result["material"] = Json.Object("name", material.name,
                "dynamicFriction", material.dynamicFriction, "staticFriction", material.staticFriction,
                "bounciness", material.bounciness, "frictionCombine", material.frictionCombine.ToString(), "bounceCombine", material.bounceCombine.ToString());
            var box = collider as BoxCollider;
            var sphere = collider as SphereCollider;
            var capsule = collider as CapsuleCollider;
            var mesh = collider as MeshCollider;
            var character = collider as CharacterController;
            var terrain = collider as TerrainCollider;
            if (box != null) result["shape"] = Json.Object("kind", "box", "center", Values.Vector(box.center), "size", Values.Vector(box.size));
            else if (sphere != null) result["shape"] = Json.Object("kind", "sphere", "center", Values.Vector(sphere.center), "radius", sphere.radius);
            else if (capsule != null) result["shape"] = Json.Object("kind", "capsule", "center", Values.Vector(capsule.center),
                "radius", capsule.radius, "height", capsule.height, "direction", capsule.direction);
            else if (character != null) result["shape"] = Json.Object("kind", "character", "center", Values.Vector(character.center),
                "radius", character.radius, "height", character.height, "skinWidth", character.skinWidth, "stepOffset", character.stepOffset, "slopeLimit", character.slopeLimit);
            else if (mesh != null) result["shape"] = Json.Object("kind", "mesh", "convex", mesh.convex, "mesh", MeshAsset(mesh.sharedMesh));
            else if (terrain != null) result["shape"] = Json.Object("kind", "terrain", "terrain", TerrainAsset(terrain.terrainData));
            else result["shape"] = Json.Object("kind", "unknown", "status", "unsupported_shape");
            return result;
        }

        public object Body(Rigidbody body)
        {
            return Json.Object("position", Values.Vector(body.position), "rotation", Values.Rotation(body.rotation),
                "velocity", Values.Vector(body.velocity), "angularVelocity", Values.Vector(body.angularVelocity),
                "mass", body.mass, "drag", body.drag, "angularDrag", body.angularDrag,
                "isKinematic", body.isKinematic, "useGravity", body.useGravity, "detectCollisions", body.detectCollisions,
                "constraints", body.constraints.ToString(), "collisionDetection", body.collisionDetectionMode.ToString(),
                "interpolation", body.interpolation.ToString(), "sleeping", body.IsSleeping(), "centerOfMass", Values.Vector(body.centerOfMass));
        }

        public object Animation(Animator animator)
        {
            var result = Json.Object("enabled", animator.enabled, "speed", animator.speed,
                "applyRootMotion", animator.applyRootMotion, "updateMode", animator.updateMode.ToString(),
                "cullingMode", animator.cullingMode.ToString(), "controller", reference(animator.runtimeAnimatorController));
            if (!animator.isInitialized || animator.runtimeAnimatorController == null)
            {
                result["status"] = "not_initialized"; return result;
            }
            var layers = new List<object>();
            for (int i = 0; i < animator.layerCount; i++)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(i);
                bool transitioning = animator.IsInTransition(i);
                var layer = Json.Object("index", i, "name", animator.GetLayerName(i), "weight", animator.GetLayerWeight(i),
                    "state", AnimationState(state), "inTransition", transitioning);
                if (transitioning)
                {
                    AnimatorTransitionInfo transition = animator.GetAnimatorTransitionInfo(i);
                    layer["next"] = AnimationState(animator.GetNextAnimatorStateInfo(i));
                    layer["transition"] = Json.Object("fullPathHash", transition.fullPathHash,
                        "normalizedTime", transition.normalizedTime, "duration", transition.duration);
                }
                layers.Add(layer);
            }
            result["layers"] = layers;
            var parameters = new List<object>();
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                object value = null;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool: value = animator.GetBool(p.nameHash); break;
                    case AnimatorControllerParameterType.Float: value = animator.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: value = animator.GetInteger(p.nameHash); break;
                }
                parameters.Add(Json.Object("name", p.name, "type", p.type.ToString(), "value", value,
                    "status", p.type == AnimatorControllerParameterType.Trigger ? "write_only_trigger" : "observed"));
            }
            result["parameters"] = parameters; return result;
        }

        private static object AnimationState(AnimatorStateInfo state)
        {
            return Json.Object("fullPathHash", state.fullPathHash, "shortNameHash", state.shortNameHash,
                "normalizedTime", state.normalizedTime, "length", state.length, "speed", state.speed,
                "speedMultiplier", state.speedMultiplier, "loop", state.loop);
        }

        private object MeshAsset(Mesh mesh)
        {
            if (mesh == null) return null;
            MeshEntry entry;
            int key = mesh.GetInstanceID();
            if (!meshes.TryGetValue(key, out entry) || !System.Object.ReferenceEquals(entry.Mesh, mesh))
            {
                entry = new MeshEntry { Mesh = mesh, RefreshedAt = -100f }; meshes[key] = entry;
            }
            // Re-read procedural meshes periodically; transforms are sampled every frame.
            if (Time.realtimeSinceStartup - entry.RefreshedAt >= 2f)
            {
                entry.RefreshedAt = Time.realtimeSinceStartup;
                object data; uint hash = 2166136261;
                if (!mesh.isReadable)
                {
                    data = Json.Object("status", "unreadable", "name", mesh.name, "vertexCount", mesh.vertexCount,
                        "bounds", Values.Bounds(mesh.bounds)); hash = (uint)mesh.vertexCount;
                }
                else if (mesh.vertexCount > 250000)
                {
                    data = Json.Object("status", "truncated", "reason", "mesh vertex limit", "vertexCount", mesh.vertexCount);
                    hash = (uint)mesh.vertexCount;
                }
                else
                {
                    Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles;
                    var flat = new float[vertices.Length * 3];
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        flat[i * 3] = vertices[i].x; flat[i * 3 + 1] = vertices[i].y; flat[i * 3 + 2] = vertices[i].z;
                    }
                    foreach (float value in flat) hash = unchecked((hash ^ (uint)value.GetHashCode()) * 16777619);
                    foreach (int value in triangles) hash = unchecked((hash ^ (uint)value) * 16777619);
                    data = Json.Object("status", "observed", "name", mesh.name, "vertices", flat, "triangles", triangles, "bounds", Values.Bounds(mesh.bounds));
                }
                if (entry.Asset == null || entry.Fingerprint != hash)
                {
                    entry.Fingerprint = hash; entry.Id = "mesh:" + (++nextAsset);
                    entry.Asset = Json.Object("type", "asset", "protocolVersion", 1, "id", entry.Id, "kind", "collision_mesh", "data", data);
                    Revision++;
                }
            }
            Assets[entry.Id] = entry.Asset;
            return Json.Object("assetId", entry.Id, "sampleAgeSeconds", Time.realtimeSinceStartup - entry.RefreshedAt);
        }

        private object TerrainAsset(TerrainData terrain)
        {
            if (terrain == null) return null;
            TerrainEntry entry;
            int key = terrain.GetInstanceID();
            if (!terrains.TryGetValue(key, out entry) || !System.Object.ReferenceEquals(entry.Terrain, terrain))
            {
                entry = new TerrainEntry { Terrain = terrain, RefreshedAt = -100 }; terrains[key] = entry;
            }
            if (Time.realtimeSinceStartup - entry.RefreshedAt >= 2f)
            {
                entry.RefreshedAt = Time.realtimeSinceStartup;
                int resolution = terrain.heightmapResolution; uint hash = 2166136261;
                object data;
                if (resolution > 1025) data = Json.Object("status", "truncated", "reason", "heightmap resolution exceeds 1025", "resolution", resolution);
                else
                {
                    float[,] heights = terrain.GetHeights(0, 0, resolution, resolution);
                    float[] values = new float[resolution * resolution];
                    for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++)
                    {
                        float value = heights[z, x]; values[z * resolution + x] = value;
                        hash = unchecked((hash ^ (uint)value.GetHashCode()) * 16777619);
                    }
                    foreach (float value in Values.Vector(terrain.size)) hash = unchecked((hash ^ (uint)value.GetHashCode()) * 16777619);
                    data = Json.Object("status", "observed", "name", terrain.name, "resolution", resolution,
                        "size", Values.Vector(terrain.size), "heights", values, "layout", "z-major normalized heights");
                }
                if (entry.Asset == null || entry.Fingerprint != hash)
                {
                    entry.Fingerprint = hash; entry.Id = "terrain:" + (++nextAsset);
                    entry.Asset = Json.Object("type", "asset", "protocolVersion", 1, "id", entry.Id, "kind", "heightfield", "data", data);
                    Revision++;
                }
            }
            Assets[entry.Id] = entry.Asset;
            return Json.Object("assetId", entry.Id, "sampleAgeSeconds", Time.realtimeSinceStartup - entry.RefreshedAt);
        }

        public static object PhysicsSettings()
        {
            var layers = new List<object>();
            for (int a = 0; a < 32; a++)
            {
                var collides = new List<int>();
                for (int b = 0; b < 32; b++) if (!Physics.GetIgnoreLayerCollision(a, b)) collides.Add(b);
                layers.Add(Json.Object("index", a, "name", LayerMask.LayerToName(a), "collidesWith", collides));
            }
            return Json.Object("gravity", Values.Vector(Physics.gravity), "layers", layers,
                "queriesHitTriggers", Physics.queriesHitTriggers, "queriesHitBackfaces", Physics.queriesHitBackfaces,
                "individualIgnoredPairs", Json.Object("status", "unavailable", "reason", "Unity 2017.4 has no Physics.GetIgnoreCollision reader"));
        }
    }
}
