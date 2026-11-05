using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterDishDelivery() { Register("deliver_dishes", DishDeliveryAvailable, DeliverDishes); }

        private bool DishDeliveryAvailable()
        {
            inventory.Refresh(chef);
            foreach (GameObject target in navigation.Targets())
                if (target.GetComponent<ServerPlateStation>() != null && Reachable(target)) return true;
            return false;
        }

        private bool DishReady(GameObject item, GameObject destination)
        {
            if (item == null || !item.activeInHierarchy || destination == null || !destination.activeInHierarchy) return false;
            var plate = item.GetComponent<ServerPlate>();
            var station = destination.GetComponent<ServerPlateStation>();
            if (plate == null || plate.IsReserved() || LogisticsInventory.Empty(item) || station == null) return false;
            var composition = plate.GetOrderComposition();
            if (composition == null) return false;
            // The same recipe and plating-step match used by native scoring.
            // A nonempty plate alone can still be raw, partial or the wrong meal.
            foreach (var flow in UnityEngine.Object.FindObjectsOfType<ServerKitchenFlowControllerBase>())
            {
                var monitor = flow.GetMonitorForTeam(station.GetTeamID());
                var orders = monitor == null ? null : monitor.OrdersController;
                if (orders == null) continue;
                OrderController.OrderID order; float remaining;
                if (orders.FindBestOrderForRecipe(composition, plate.GetPlatingStep(), out order, out remaining)) return true;
            }
            return false;
        }

        private GameObject ServingWindow(GameObject item)
        {
            GameObject best = null; float distance = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
                if (inventory.DistanceTo(target) < distance && DishReady(item, target) &&
                    ActionObservations.Occupant(target) == null && CanDeliver(item, target))
                { best = target; distance = inventory.DistanceTo(target); }
            return best;
        }

        private LogisticsItem PlatedDish(out GameObject destination)
        {
            RefreshWorld(); LogisticsItem best = null; destination = null; float distance = Single.MaxValue;
            GameObject held = ActionObservations.Held(chef);
            foreach (LogisticsItem item in inventory.Items)
            {
                if (item.Supply || item.InUse || item.Item.GetComponent<ServerPlate>() == null || !Obtainable(item, "move_item")) continue;
                GameObject window = ServingWindow(item.Item);
                if (window == null) continue;
                // Do this before comparing walking costs: a ready plate in P2's
                // own hands never needs another pickup or a parking detour.
                if (System.Object.ReferenceEquals(item.Item, held)) { destination = window; return item; }
                float cost = item.Distance + inventory.DistanceTo(window);
                if (cost < distance) { best = item; destination = window; distance = cost; }
            }
            return best;
        }

        private bool CanTakeDish(GameObject item, GameObject destination)
        {
            RefreshWorld();
            if (!DishReady(item, destination)) return false;
            foreach (LogisticsItem entry in inventory.Items)
                if (System.Object.ReferenceEquals(entry.Item, item)) return !entry.InUse && Obtainable(entry, "move_item");
            return false;
        }

        private IEnumerable<TaskStep> FreeHandsForDish(GameObject item, GameObject source)
        {
            var excluded = new HashSet<GameObject>();
            while (System.Object.ReferenceEquals(ActionObservations.Held(chef), item))
            {
                GameObject counter = null; float searchUntil = Time.realtimeSinceStartup + 1;
                yield return Until("checking_nearby_counters", delegate {
                    counter = Parking(item, excluded: excluded, maxDistance: 5);
                    // A distant/incomplete map frontier must not prevent the
                    // requested fallback after trying the nearby counters.
                    return counter != null || botSearchComplete || Time.realtimeSinceStartup >= searchUntil;
                });
                if (counter == null)
                {
                    // Extend the normal placement primitive with a checked floor
                    // release. No task changes the carrier or an item's pose.
                    yield return new TaskStep { Phase = "dropping_held_item", Command = () =>
                        new ControlCommand { Operation = "place_at", GroundDrop = true, GroundDropAwayFrom = navigation.RegisterItem(source) } };
                    yield break;
                }
                TaskStep placement = Do("place_at", counter); placement.ReplanOnBlock = true;
                yield return placement;
                if (placement.Succeeded) yield break;
                excluded.Add(counter);
            }
        }

        private IEnumerable<TaskStep> DeliverDishes()
        {
            while (true)
            {
                LogisticsItem dish = null; GameObject destination = null;
                yield return Until("waiting_for_plated_dish", delegate { dish = PlatedDish(out destination); return dish != null; });
                GameObject item = dish.Item;
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), item))
                {
                    TaskStep pickup = Do("pick_up", dish.Source);
                    pickup.ReplanOnBlock = true;
                    pickup.FreeHands = held => FreeHandsForDish(held, dish.Source);
                    pickup.ReplanForHeld = held => ServingWindow(held) != null;
                    pickup.AbandonBeforePress = () => !CanTakeDish(item, destination);
                    Func<bool> ready = pickup.Ready;
                    pickup.Ready = () => CanTakeDish(item, destination) && ready();
                    yield return pickup;
                    if (!pickup.Succeeded) continue;
                }
                if (!System.Object.ReferenceEquals(ActionObservations.Held(chef), item)) continue;
                TaskStep serve = Do("place_at", destination);
                serve.ReplanOnBlock = true;
                serve.AbandonBeforePress = () => !DishReady(item, destination);
                Func<bool> canServe = serve.Ready;
                serve.Ready = () => System.Object.ReferenceEquals(ActionObservations.Held(chef), item) && DishReady(item, destination) && canServe();
                yield return serve;
                if (serve.Succeeded) yield break;
            }
        }
    }
}
