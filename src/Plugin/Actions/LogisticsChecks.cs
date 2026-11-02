using System;
using System.Collections;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // Query native placement predicates for prospective cargo without changing
    // either chef's actual carrier, station contents, or input state.
    internal static class LogisticsChecks
    {
        private sealed class CarrierView : ICarrier
        {
            private readonly GameObject actor, item;
            public CarrierView(PlayerControls chef, GameObject cargo) { actor = chef.gameObject; item = cargo; }
            public GameObject InspectCarriedItem() { return item; }
            public GameObject AccessGameObject() { return actor; }
            public void RegisterCarriedItemChangeCallback(VoidGeneric<GameObject, GameObject> callback) { throw new InvalidOperationException("read_only_carrier"); }
            public void UnregisterCarriedItemChangeCallback(VoidGeneric<GameObject, GameObject> callback) { throw new InvalidOperationException("read_only_carrier"); }
            public void CarryItem(GameObject cargo) { throw new InvalidOperationException("read_only_carrier"); }
            public void DestroyCarriedItem() { throw new InvalidOperationException("read_only_carrier"); }
            public GameObject TakeItem() { throw new InvalidOperationException("read_only_carrier"); }
        }
        public static GameObject Payload(LogisticsItem entry)
        {
            var stack = entry.Item.GetComponent<ServerStack>();
            return entry.Item.GetComponent<CleanPlateStack>() != null ? (stack == null ? null : stack.InspectTopOfStack()) : entry.Item;
        }
        public static string Collect(PlayerControls actor, LogisticsItem entry, string operation)
        {
            if (entry.Item == null || entry.Source == null || !entry.Source.activeInHierarchy) return "source_unavailable";
            if (entry.InUse) return "item_in_use";
            GameObject payload = Payload(entry), held = ActionObservations.Held(actor);
            if (held != null) return System.Object.ReferenceEquals(held, payload) ? null : "hands_full";
            if (entry.Distance == Single.MaxValue) return "source_unreachable";
            if (payload == null) return "source_empty";
            if (entry.Item.GetComponent<CleanPlateStack>() != null && operation != "get_plate" && operation != "clear_station") return "source_dispenses_plates_use_get_plate";
            var pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(entry.Source);
            return pickup != null && pickup.CanHandlePickup(new CarrierView(actor, null)) ? null : "pickup_unavailable";
        }
        public static string Delivery(PlayerControls actor, string operation, GameObject item, GameObject destination, out string delivery, bool supplyPrefab = false)
        {
            delivery = null;
            if (item == null || destination == null || !destination.activeInHierarchy) return "destination_unavailable";
            if (destination.GetComponent<ServerRubbishBin>() != null) return "logistics_cannot_discard";
            GameObject occupant = ActionObservations.Occupant(destination);
            var contents = ActionObservations.Container(destination);
            if (operation != "stage_item" && item.GetComponent<IngredientPropertiesComponent>() != null && contents != null)
            {
                var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(item);
                var composition = ActionObservations.Composition(item);
                if (transfer == null || composition == null || !transfer.CanTransferToContainer(contents) || !contents.CanAddIngredient(composition)) return "incompatible_ingredient_destination";
                delivery = "add_ingredient";
            }
            else
            {
                if (destination.GetComponent<ServerAttachStation>() == null && destination.GetComponent<ServerWashingStation>() == null) return "destination_is_not_surface";
                if (occupant != null && !(item.GetComponent<ServerPlate>() != null && ActionObservations.StackCount(occupant) > 0)) return "destination_occupied";
                delivery = "place_at";
            }
            var placement = PlayerControlsHelper.GetControllingPlacementHandler_Client(destination);
            Vector3 delta = destination.transform.position - actor.transform.position; delta.y = 0; delta.Normalize();
            var context = new PlacementContext(PlacementContext.Source.Player);
            if (placement != null && placement.CanHandlePlacement(new CarrierView(actor, item), new Vector2(delta.x, delta.z), context)) return null;
            // Prefabs have PhysicalAttachment but receive their IAttachment
            // implementations only on network spawn. For an empty native holder,
            // query the same placement vetoes with the prospective prefab. Actual
            // cargo goes through the full native predicate again after pickup.
            var station = placement as ClientAttachStation;
            if (!supplyPrefab || delivery != "place_at" || station == null || station.InspectItem() != null || item.GetComponent<PhysicalAttachment>() == null) return "placement_unavailable";
            foreach (Delegate allowed in ActionObservations.Read<IEnumerable>(station, "m_allowPlacementCallbacks"))
                if (!(bool)allowed.DynamicInvoke(item, context)) return "placement_unavailable";
            return null;
        }
    }
}
