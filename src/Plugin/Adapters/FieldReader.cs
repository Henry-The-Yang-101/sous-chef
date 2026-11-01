using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SousChef.Plugin.Adapters
{
    public sealed class FieldReader
    {
        private sealed class BoundSpec
        {
            public AdapterSpec Spec;
            public FieldInfo[] Fields;
            public string[] Keys;
        }
        private readonly Dictionary<Type, List<BoundSpec>> cache = new Dictionary<Type, List<BoundSpec>>();
        private readonly List<object> parents = new List<object>(24);
        private readonly Func<Object, object> reference;
        public readonly Dictionary<string, int> UnsupportedValues = new Dictionary<string, int>();
        public readonly HashSet<string> MissingFields = new HashSet<string>();
        public readonly HashSet<string> UsedAdapters = new HashSet<string>();
        public int TruncatedValues;
        public int Errors;

        public FieldReader(Func<Object, object> objectReference) { reference = objectReference; }
        public void BeginFrame()
        {
            UnsupportedValues.Clear(); MissingFields.Clear(); UsedAdapters.Clear(); TruncatedValues = 0; Errors = 0;
        }

        public bool Supports(Type type) { return Bind(type).Count > 0; }

        public object Read(object value)
        {
            int budget = 8192;
            parents.Clear();
            try { return Record(value, 0, parents, ref budget); }
            finally { parents.Clear(); }
        }

        private object Record(object value, int depth, List<object> parents, ref int budget)
        {
            if (value == null) return null;
            if (--budget < 0 || depth > 24)
            {
                TruncatedValues++; return Json.Object("status", "truncated", "reason", "adapter depth/value budget");
            }
            foreach (object parent in parents)
                if (System.Object.ReferenceEquals(parent, value)) return Json.Object("status", "cycle", "type", value.GetType().FullName);
            Type type = value.GetType();
            if (type.IsEnum) return value.ToString();
            if (value is char) return value.ToString();
            if (type.IsPrimitive || value is string || value is decimal) return value;
            if (value is Vector3) return Values.Vector((Vector3)value);
            if (value is Vector2) { Vector2 v = (Vector2)value; return new[] { v.x, v.y }; }
            if (value is Quaternion) return Values.Rotation((Quaternion)value);
            if (value is Bounds) return Values.Bounds((Bounds)value);
            if (value is LayerMask) return ((LayerMask)value).value;
            if (value is Color) { Color c = (Color)value; return new[] { c.r, c.g, c.b, c.a }; }
            // Scene references are never recursively expanded.
            if (value is Component || value is GameObject) return reference((Object)value);
            var specs = Bind(type);
            if (specs.Count > 0)
            {
                var result = Json.Object("type", type.FullName);
                if (value is Object)
                {
                    var asset = (Object)value;
                    if (asset == null) return null;
                    result["name"] = asset.name;
                    result["assetInstanceId"] = asset.GetInstanceID();
                }
                parents.Add(value);
                var fields = new Dictionary<string, object>();
                foreach (BoundSpec bound in specs)
                {
                    UsedAdapters.Add(bound.Spec.TypeName);
                    for (int i = 0; i < bound.Spec.Fields.Length; i++)
                    {
                        string key = bound.Keys[i];
                        FieldInfo field = bound.Fields[i];
                        if (field == null)
                        {
                            MissingFields.Add(key); fields[key] = Json.Object("status", "missing_field"); continue;
                        }
                        try
                        {
                            object child = field.GetValue(value);
                            // A queue of transitions contains live iterators, not data.
                            if (bound.Spec.TypeName == "ClientDynamicFlowController" && field.Name == "m_phaseQueue")
                                fields[key] = Json.Object("count", ((ICollection)child).Count, "bodies", "unobserved");
                            else fields[key] = Record(child, depth + 1, parents, ref budget);
                        }
                        catch (Exception e) { Errors++; fields[key] = Json.Object("status", "read_error", "error", e.GetType().Name); }
                    }
                }
                parents.RemoveAt(parents.Count - 1);
                result["fields"] = fields; return result;
            }
            if (value is Object) return reference((Object)value);
            if (value is IDictionary)
            {
                var entries = new List<object>(); parents.Add(value);
                foreach (DictionaryEntry pair in (IDictionary)value)
                {
                    if (entries.Count >= 4096 || budget <= 0) { entries.Add(Truncated()); break; }
                    entries.Add(Json.Object("key", Record(pair.Key, depth + 1, parents, ref budget),
                        "value", Record(pair.Value, depth + 1, parents, ref budget)));
                }
                parents.RemoveAt(parents.Count - 1); return entries;
            }
            // The game's FastList has public backing storage but is NOT IEnumerable.
            // Read only _size live entries, not stale objects in spare capacity.
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Collections.Generic.FastList`1")
            {
                var array = (Array)type.GetField("_items").GetValue(value);
                int liveCount = (int)type.GetField("_size", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
                var entries = new List<object>(); parents.Add(value);
                for (int i = 0; i < liveCount && i < array.Length; i++)
                {
                    if (entries.Count >= 4096 || budget <= 0) { entries.Add(Truncated()); break; }
                    entries.Add(Record(array.GetValue(i), depth + 1, parents, ref budget));
                }
                parents.RemoveAt(parents.Count - 1); return entries;
            }
            // Restrict enumeration to ordinary collections, not arbitrary game iterators.
            if (value is IList || IsKnownCollection(type))
            {
                var entries = new List<object>(); parents.Add(value);
                foreach (object child in (IEnumerable)value)
                {
                    if (entries.Count >= 4096 || budget <= 0) { entries.Add(Truncated()); break; }
                    entries.Add(Record(child, depth + 1, parents, ref budget));
                }
                parents.RemoveAt(parents.Count - 1); return entries;
            }
            int count; UnsupportedValues.TryGetValue(type.FullName, out count); UnsupportedValues[type.FullName] = count + 1;
            return Json.Object("status", "unsupported_value", "type", type.FullName);
        }

        // Expand the root component only. References inside it remain references.
        public object ReadComponent(Component component)
        {
            var specs = Bind(component.GetType());
            var fields = new Dictionary<string, object>(specs.Count);
            foreach (BoundSpec bound in specs)
            {
                UsedAdapters.Add(bound.Spec.TypeName);
                var values = new Dictionary<string, object>(bound.Fields.Length);
                for (int i = 0; i < bound.Fields.Length; i++)
                {
                    string name = bound.Spec.Fields[i]; FieldInfo field = bound.Fields[i];
                    if (field == null)
                    {
                        MissingFields.Add(bound.Keys[i]);
                        values[name] = Json.Object("status", "missing_field"); continue;
                    }
                    try
                    {
                        object value = field.GetValue(component);
                        values[name] = bound.Spec.TypeName == "ClientDynamicFlowController" && name == "m_phaseQueue"
                            ? Json.Object("count", value == null ? 0 : ((ICollection)value).Count, "bodies", "unobserved") : Read(value);
                    }
                    catch (Exception e) { Errors++; values[name] = Json.Object("status", "read_error", "error", e.GetType().Name); }
                }
                fields[bound.Spec.TypeName] = Json.Object("domain", bound.Spec.Domain, "fields", values);
            }
            return fields;
        }

        private object Truncated() { TruncatedValues++; return Json.Object("status", "truncated", "reason", "collection budget"); }
        private static bool IsKnownCollection(Type type)
        {
            string name = type.FullName;
            return name.StartsWith("System.Collections.Generic.Queue`1", StringComparison.Ordinal);
        }

        private List<BoundSpec> Bind(Type type)
        {
            List<BoundSpec> result;
            if (cache.TryGetValue(type, out result)) return result;
            result = new List<BoundSpec>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                AdapterSpec spec;
                if (!AdapterCatalog.Specs.TryGetValue(t.FullName, out spec)) continue;
                var fields = new FieldInfo[spec.Fields.Length];
                var keys = new string[fields.Length];
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = t.GetField(spec.Fields[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    keys[i] = spec.TypeName + "." + spec.Fields[i];
                }
                result.Add(new BoundSpec { Spec = spec, Fields = fields, Keys = keys });
            }
            cache.Add(type, result); return result;
        }
    }
}
