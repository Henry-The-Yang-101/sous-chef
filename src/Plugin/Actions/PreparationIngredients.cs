using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class PreparationIngredient
    {
        public GameObject Prefab;
        public string Id, Name;
    }

    // A kitchen's configured food types are independent of its current cargo.
    // Automatic belt spawners are not pickup destinations: their prefabs must
    // never be added to the live logistics inventory or used as pickup targets.
    internal sealed class PreparationIngredients
    {
        private TriggerAttachedSpawn[] automatic;
        private float nextScan;
        public void Reset() { automatic = null; nextScan = 0; }

        public List<PreparationIngredient> Observe(IEnumerable<GameObject> targets)
        {
            var result = new List<PreparationIngredient>();
            var seen = new HashSet<string>();
            foreach (GameObject target in targets)
            {
                var dispenser = target.GetComponent<PickupItemSpawner>();
                if (dispenser != null) Add(result, seen, dispenser.m_itemPrefab);
                var switcher = target.GetComponent<PickupItemSwitcher>();
                if (switcher != null && switcher.m_itemPrefabs != null)
                    foreach (GameObject prefab in switcher.m_itemPrefabs) Add(result, seen, prefab);
            }
            // These sources need no collider or interaction handler. Re-scan at
            // a bounded cadence to discover newly activated/created sources;
            // round reset clears the cache and removal takes effect immediately.
            if (automatic == null || Time.realtimeSinceStartup >= nextScan)
            {
                automatic = UnityEngine.Object.FindObjectsOfType<TriggerAttachedSpawn>();
                nextScan = Time.realtimeSinceStartup + 1;
            }
            foreach (var source in automatic)
            {
                if (source == null || !source.gameObject.activeInHierarchy || source.m_attachmentPrefabs == null) continue;
                foreach (var entry in source.m_attachmentPrefabs)
                    if (entry != null) Add(result, seen, entry.AttachmentPrefab);
            }
            return result;
        }

        private static void Add(List<PreparationIngredient> result, HashSet<string> seen, GameObject prefab)
        {
            string id, name;
            if (LogisticsInventory.IngredientIdentity(prefab, out id, out name) && seen.Add(id))
                result.Add(new PreparationIngredient { Prefab = prefab, Id = id, Name = name });
        }
    }
}
