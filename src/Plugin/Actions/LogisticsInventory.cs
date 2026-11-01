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
        public LogisticsInventory(NavigationActions nav) { navigation = nav; }
        public void Reset() { Items.Clear(); Errors.Clear(); homes.Clear(); homeItems.Clear(); }
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
        public static bool HeldByOther(PlayerControls chef, GameObject item)
        {
            foreach (PlayerControls player in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                if (player != chef && System.Object.ReferenceEquals(ActionObservations.Held(player), item)) return true;
            return false;
        }
        public void Refresh(PlayerControls chef, Json.Record currentCatalog = null)
        {
            Items.Clear(); Errors.Clear(); var distances = new Dictionary<string, float>();
            var catalog = currentCatalog ?? (Json.Record)navigation.Available(chef);
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
            var ingredient = item.GetComponent<IngredientPropertiesComponent>();
            if (ingredient != null)
            {
                var definition = ActionObservations.Read<UnityEngine.Object>(ingredient, "m_ingredientOrderNode");
                if (definition != null)
                {
                    entry.IngredientId = "i" + ActionObservations.Read<int>(definition, "m_uID");
                    entry.IngredientName = definition.name;
                }
            }
            var interaction = source.GetComponent<ClientInteractable>();
            entry.InUse = HeldByOther(chef, item) || (interaction != null && interaction.InteractorCount() > 0);
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
