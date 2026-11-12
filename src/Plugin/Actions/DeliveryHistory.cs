using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Exclude an already handed-off portion, not the cookware for its lifetime.
    // A filled vessel becomes reusable after its contents have been removed.
    internal sealed class DeliveryHistory
    {
        private readonly Dictionary<GameObject, bool> items = new Dictionary<GameObject, bool>();
        public void Add(GameObject item)
        {
            if (item != null) items[item] = !LogisticsInventory.Empty(item);
        }
        public bool Contains(GameObject item)
        {
            bool filled;
            if (item == null || !items.TryGetValue(item, out filled)) return false;
            if (filled && LogisticsInventory.Empty(item)) { items.Remove(item); return false; }
            if (!filled && !LogisticsInventory.Empty(item)) items[item] = true;
            return true;
        }
        public void Observe()
        {
            foreach (var item in new List<GameObject>(items.Keys))
                if (item == null || !Contains(item)) items.Remove(item);
        }
        public void Clear() { items.Clear(); }
    }
}
