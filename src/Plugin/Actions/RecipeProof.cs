using System;
using System.Collections.Generic;
using SousChef.Core;
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
        public static AssembledDefinitionNode MixingInputs(GameObject item)
        {
            var contents = item == null ? null : item.GetComponent<ServerIngredientContainer>();
            return contents == null ? null : new CompositeAssembledNode { m_composition = contents.GetContents() };
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
        public static object Describe(AssembledDefinitionNode node)
        {
            var ingredient = node as IngredientAssembledNode;
            if (ingredient != null) return Json.Object("kind", "ingredient", "id", "i" + ingredient.m_ingriedientOrderNode.m_uID, "name", ingredient.m_ingriedientOrderNode.name);
            var composite = node as CompositeAssembledNode;
            if (composite == null) return null;
            var parts = new List<object>(); foreach (var part in composite.m_composition) parts.Add(Describe(part));
            var cooked = node as CookedCompositeAssembledNode;
            return Json.Object("kind", cooked != null ? "cooked" : node is MixedCompositeAssembledNode ? "mixed" : "assembly", "components", parts,
                "cookingStep", cooked == null ? null : cooked.m_cookingStep.name,
                "cookingStepId", cooked == null ? null : (object)cooked.m_cookingStep.m_uID);
        }
    }
}
