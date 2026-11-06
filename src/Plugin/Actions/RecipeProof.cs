using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Pure recipe algebra. Plain assembly groupings are flattened; processing
    // boundaries, native ingredient IDs, cooking methods and multiplicity stay
    // intact. Never mutate a live node (m_freeObject can forward edits to Unity).
    internal static class RecipeProof
    {
        public static AssembledDefinitionNode Composition(GameObject item)
        {
            if (item == null || !item.activeInHierarchy) return null;
            var plate = item.GetComponent<ServerPlate>();
            var value = plate == null ? ActionObservations.Composition(item) : plate.GetOrderComposition();
            return value == null ? null : value.Simpilfy();
        }
        public static List<AssembledDefinitionNode> Parts(AssembledDefinitionNode value)
        {
            var result = new List<AssembledDefinitionNode>(); Append(result, value); return result;
        }
        private static void Append(List<AssembledDefinitionNode> result, AssembledDefinitionNode value)
        {
            if (value == null || value == AssembledDefinitionNode.NullNode) return;
            value = value.Simpilfy();
            if (value == AssembledDefinitionNode.NullNode) return;
            if (value.GetType() == typeof(CompositeAssembledNode))
                foreach (var part in ((CompositeAssembledNode)value).m_composition) Append(result, part);
            else result.Add(value);
        }
        public static bool Empty(AssembledDefinitionNode value) { return value != null && Parts(value).Count == 0; }
        public static bool Supported(AssembledDefinitionNode value, int depth = 0)
        {
            if (value == null || depth > 12) return false;
            if (value == AssembledDefinitionNode.NullNode) return true;
            var composite = value as CompositeAssembledNode;
            if (composite == null) return value is IngredientAssembledNode;
            if (composite.m_optional != null && composite.m_optional.Length != 0) return false;
            if (composite.m_composition == null || composite.m_composition.Length > 24) return false;
            foreach (var part in composite.m_composition) if (!Supported(part, depth + 1)) return false;
            return value.GetType() == typeof(CompositeAssembledNode) || value is CookedCompositeAssembledNode || value is MixedCompositeAssembledNode;
        }
        public static AssembledDefinitionNode Combine(AssembledDefinitionNode a, AssembledDefinitionNode b)
        {
            var parts = Parts(a); parts.AddRange(Parts(b));
            return new CompositeAssembledNode { m_composition = parts.ToArray() };
        }
        public static bool Equal(AssembledDefinitionNode a, AssembledDefinitionNode b)
        { return a != null && b != null && Parts(a).Count == Parts(b).Count && Subset(a, b); }
        public static bool Subset(AssembledDefinitionNode actual, AssembledDefinitionNode desired)
        {
            if (actual == null || desired == null) return false;
            var remaining = Parts(desired);
            foreach (var part in Parts(actual))
            {
                int match = remaining.FindIndex(candidate => AssembledDefinitionNode.Matching(part, candidate));
                if (match < 0) return false;
                remaining.RemoveAt(match);
            }
            return true;
        }
        public static bool Advances(AssembledDefinitionNode current, AssembledDefinitionNode addition, AssembledDefinitionNode goal)
        { return current != null && addition != null && !Empty(addition) && Subset(current, goal) && Subset(Combine(current, addition), goal); }
    }
}
