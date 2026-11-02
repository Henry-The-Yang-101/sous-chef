using System;
using System.Collections.Generic;
using System.Text;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed class LogisticsItem
    {
        public GameObject Item, Source;
        public string ItemId, SourceId, IngredientId, IngredientName, UtensilType;
        public bool Supply, CleanPlate, Empty, InUse;
        public float Distance = Single.MaxValue;
        public object Describe()
        {
            return Json.Object("id", ItemId, "label", Item.name, "sourceId", SourceId, "sourceInstanceId", Source.GetInstanceID(),
                "objectInstanceId", Item.GetInstanceID(), "ingredientId", IngredientId, "ingredientName", IngredientName,
                "utensilType", UtensilType, "cleanPlate", CleanPlate, "empty", Empty, "inUse", InUse,
                "supply", Supply, "routeDistance", Distance == Single.MaxValue ? null : (object)Distance);
        }
    }

    internal sealed class LogisticsInventory
    {
        public readonly List<LogisticsItem> Items = new List<LogisticsItem>();
        public readonly List<object> Errors = new List<object>();
        private readonly Dictionary<int, GameObject> homes = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, GameObject> homeItems = new Dictionary<int, GameObject>();
        private readonly NavigationActions navigation;
        private readonly Dictionary<string, float> distances = new Dictionary<string, float>();
        private readonly List<GameObject> otherHeldItems = new List<GameObject>();
        public LogisticsInventory(NavigationActions nav) { navigation = nav; }
        public void Reset() { Items.Clear(); Errors.Clear(); homes.Clear(); homeItems.Clear(); distances.Clear(); otherHeldItems.Clear(); }
        public float DistanceTo(GameObject target)
        { float cost; string id = Id(target); return id != null && distances.TryGetValue(id, out cost) ? cost : Single.MaxValue; }
        public static string Normalize(string text)
        {
            var result = new StringBuilder();
            foreach (char c in text ?? "") if (Char.IsLetterOrDigit(c)) result.Append(Char.ToLowerInvariant(c));
            return result.ToString();
        }
        public static string CanonicalUtensil(string text)
        {
            string value = Normalize(text);
            if (value.StartsWith("utensil")) value = value.Substring(7);
            switch (value)
            {
                case "cookingpot": case "saucepan": case "pot": return "pot";
                case "fryingpan": case "pan": return "pan";
                case "mixingbowl": case "bowl": return "bowl";
                case "bakingtray": case "tray": return "tray";
                case "fireextinguisher": case "extinguisher": return "extinguisher";
                default: return value;
            }
        }
        public static string UtensilType(GameObject item)
        {
            if (item == null || item.GetComponent<ServerPlate>() != null || item.GetComponent<IngredientPropertiesComponent>() != null) return null;
            var properties = item.GetComponent<ItemPropertiesComponent>();
            if (properties == null) return null;
            var definition = ActionObservations.Read<UnityEngine.Object>(properties, "m_itemDefinition");
            return definition == null || String.IsNullOrEmpty(definition.name) ? null : CanonicalUtensil(definition.name);
        }
        public static bool IngredientIdentity(GameObject item, out string id, out string name)
        {
            id = name = null;
            if (item == null) return false;
            var ingredient = item.GetComponent<IngredientPropertiesComponent>();
            var definition = ingredient == null ? null : ActionObservations.Read<UnityEngine.Object>(ingredient, "m_ingredientOrderNode");
            // Assembly bases such as buns/tortillas are preparation containers,
            // not IngredientPropertiesComponents or WorkableItems. A filled base
            // is an assembled meal, never a substitute for the empty ingredient.
            var preparation = item.GetComponent<PreparationContainer>();
            if (definition == null && preparation != null)
            {
                var contents = item.GetComponent<ServerIngredientContainer>();
                if (contents != null && contents.GetContentsCount() != 0) return false;
                definition = ActionObservations.Read<UnityEngine.Object>(preparation, "m_ingredientOrderNode");
            }
            if (definition != null)
            {
                id = "i" + ActionObservations.Read<int>(definition, "m_uID"); name = definition.name; return true;
            }
            // Raw choppable foods (e.g. Chocolate) have no IOrderDefinition.
            // Their native output prefab is shared by the supply and spawned item.
            // Keep this identity separate from the processed ingredient's order ID.
            var workable = item.GetComponent<WorkableItem>();
            GameObject output = workable == null ? null : workable.GetNextPrefab();
            if (output == null) return false;
            id = "raw" + output.GetInstanceID(); name = item.name;
            if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7).TrimEnd();
            return true;
        }
        public static bool Empty(GameObject item)
        {
            if (item == null) return false;
            var contents = item.GetComponent<ServerIngredientContainer>();
            var plate = item.GetComponent<ServerPlate>();
            if (contents == null && plate != null) contents = ActionObservations.Read<ServerIngredientContainer>(plate, "m_ingredientContainer");
            // Ordinary tools have no ingredient container. Plates must have an
            // observed container before we can call them empty and clean.
            return contents != null ? contents.GetContentsCount() == 0 : plate == null;
        }
        private bool HeldByOther(GameObject item)
        {
            foreach (GameObject held in otherHeldItems)
                if (System.Object.ReferenceEquals(held, item)) return true;
            return false;
        }
        public void Refresh(PlayerControls chef, Json.Record currentCatalog = null)
        {
            Items.Clear(); Errors.Clear(); distances.Clear();
            var catalog = currentCatalog ?? (Json.Record)navigation.Available(chef);
            // One synchronous observation pass: discover carriers once, rather
            // than scanning all Unity objects again for each inventory item.
            otherHeldItems.Clear();
            foreach (PlayerControls player in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
            {
                if (player == chef) continue;
                GameObject otherHeld = ActionObservations.Held(player);
                if (otherHeld != null) otherHeldItems.Add(otherHeld);
            }
            foreach (Json.Record d in (List<object>)catalog["destinations"]) distances[(string)d["id"]] = (float)d["routeDistance"];
            GameObject held = ActionObservations.Held(chef);
            if (held != null) navigation.RegisterItem(held);
            var targets = navigation.Targets(); var seen = new HashSet<int>();
            // Resolve explicit holder edges before loose objects. Hierarchy or
            // proximity does not establish that a station owns an ingredient.
            foreach (GameObject source in targets)
            {
                GameObject occupant = ActionObservations.Occupant(source);
                if (occupant != null) TryAdd(chef, occupant, source, false, distances, seen);
            }
            foreach (GameObject source in targets)
            {
                var supply = source.GetComponent<PickupItemSpawner>();
                if (supply != null && supply.m_itemPrefab != null) TryAdd(chef, supply.m_itemPrefab, source, true, distances, seen);
                else if (source.GetComponent<PhysicalAttachment>() != null || source.GetComponent<ServerStack>() != null || System.Object.ReferenceEquals(source, held))
                    TryAdd(chef, source, source, false, distances, seen);
            }
        }
        private void TryAdd(PlayerControls chef, GameObject item, GameObject source, bool supply, Dictionary<string, float> distances, HashSet<int> seen)
        {
            try { Add(chef, item, source, supply, distances, seen); }
            catch (Exception) { Errors.Add(Json.Object("sourceId", Id(source), "reason", "logistics_observation_unavailable")); }
        }
        private void Add(PlayerControls chef, GameObject item, GameObject source, bool supply, Dictionary<string, float> distances, HashSet<int> seen)
        {
            if (!supply && !seen.Add(item.GetInstanceID())) return;
            var entry = new LogisticsItem { Item = item, Source = source, Supply = supply, Empty = Empty(item),
                SourceId = Id(source), ItemId = supply ? Id(source) : navigation.RegisterItem(item), UtensilType = UtensilType(item) };
            GameObject plateItem = item;
            var stack = item.GetComponent<ServerStack>();
            if (stack != null && item.GetComponent<CleanPlateStack>() != null) plateItem = stack.InspectTopOfStack();
            entry.CleanPlate = plateItem != null && plateItem.GetComponent<ServerPlate>() != null && Empty(plateItem);
            IngredientIdentity(item, out entry.IngredientId, out entry.IngredientName);
            var interaction = source.GetComponent<ClientInteractable>();
            entry.InUse = HeldByOther(item) || (interaction != null && interaction.InteractorCount() > 0);
            if (System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) entry.Distance = 0;
            else { float cost; if (distances.TryGetValue(entry.SourceId ?? "", out cost)) entry.Distance = cost;
                else if (distances.TryGetValue(entry.ItemId ?? "", out cost)) { entry.Distance = cost; entry.Source = item; entry.SourceId = entry.ItemId; } }
            Items.Add(entry);
            if (entry.UtensilType != null && !supply && source != item && source.GetComponent<ServerAttachStation>() != null &&
                (!homeItems.ContainsKey(item.GetInstanceID()) || !System.Object.ReferenceEquals(homeItems[item.GetInstanceID()], item)))
            { homes[item.GetInstanceID()] = source; homeItems[item.GetInstanceID()] = item; }
        }
        public string Id(GameObject go)
        {
            var identity = (Json.Record)navigation.TargetIdentity(go);
            return identity == null ? null : (string)identity["id"];
        }
        public GameObject Home(GameObject item)
        {
            GameObject home, recorded;
            return item != null && homes.TryGetValue(item.GetInstanceID(), out home) && homeItems.TryGetValue(item.GetInstanceID(), out recorded) &&
                System.Object.ReferenceEquals(recorded, item) && home != null && home.activeInHierarchy ? home : null;
        }
        public static bool IngredientMatches(LogisticsItem entry, string selector)
        {
            if (entry.IngredientId == null) return false;
            if (String.Equals(entry.IngredientId, selector, StringComparison.Ordinal)) return true;
            string name = Normalize(entry.IngredientName), requested = Normalize(selector);
            return name == requested || (name.EndsWith("raw") && name.Substring(0, name.Length - 3) == requested);
        }
    }
}
