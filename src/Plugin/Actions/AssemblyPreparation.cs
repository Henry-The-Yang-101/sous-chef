using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Follow native work-prefab edges, never ingredient names or recipe-specific
    // rules. Raw food may have no order composition until its final work stage.
    internal static class AssemblyPreparation
    {
        public static GameObject Output(GameObject item)
        {
            var seen = new HashSet<GameObject>();
            while (item != null && seen.Add(item))
            {
                var work = item.GetComponent<WorkableItem>();
                if (work == null) return item;
                item = work.GetNextPrefab();
            }
            return null;
        }

        public static AssembledDefinitionNode Composition(GameObject item)
        {
            var output = Output(item);
            // Prefab assets need not be active and may not have server-side
            // containers. Ingredient/preparation definitions remain authoritative.
            if (output == null) return null;
            var value = ActionObservations.Composition(output);
            if (value != null) return value.Simpilfy();
            var preparation = output.GetComponent<PreparationContainer>();
            var definition = preparation == null ? null : ActionObservations.Read<OrderDefinitionNode>(preparation, "m_ingredientOrderNode");
            if (definition != null) return definition.Simpilfy();
            return null;
        }

        public static bool Produces(GameObject item, AssembledDefinitionNode goal)
        { return RecipeProof.Equal(Composition(item), goal); }
    }
}
