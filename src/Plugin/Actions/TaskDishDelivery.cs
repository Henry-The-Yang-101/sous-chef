using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterDishDelivery() { Register("deliver_completed_dishes", DishDeliveryAvailable, DeliverCompletedDishes); }

        private bool DishDeliveryAvailable()
        {
            inventory.Refresh(chef);
            foreach (GameObject target in navigation.Targets())
                if (target.GetComponent<ServerPlateStation>() != null && Reachable(target)) return true;
            return false;
        }

        private bool DishReady(GameObject item, GameObject destination)
        { float expiry; return DishExpiry(item, destination, out expiry); }

        private bool DishExpiry(GameObject item, GameObject destination, out float expiry)
        {
            expiry = Single.MaxValue;
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
                OrderController.OrderID order; float fraction;
                if (!orders.FindBestOrderForRecipe(composition, plate.GetPlatingStep(), out order, out fraction)) continue;
                // The matcher returns a fraction of the ticket's lifetime.
                // Compare actual seconds so differing order lifetimes cannot
                // invert which ticket will expire first.
                foreach (var active in ActionObservations.Read<List<OrderController.ServerOrderData>>(orders, "m_activeOrders"))
                    if (active.ID.Equals(order) && active.Remaining >= 0 && active.Remaining < expiry) expiry = active.Remaining;
            }
            return expiry != Single.MaxValue;
        }

        private GameObject ServingWindow(GameObject item)
        { float expiry; return ServingWindow(item, out expiry); }

        private GameObject ServingWindow(GameObject item, out float expiry)
        {
            GameObject best = null; float distance = Single.MaxValue; expiry = Single.MaxValue;
            foreach (GameObject target in navigation.Targets())
            {
                float candidate;
                if (Reachable(target) && DishExpiry(item, target, out candidate) &&
                    (candidate < expiry || candidate == expiry && inventory.DistanceTo(target) < distance) &&
                    ActionObservations.Occupant(target) == null && CanDeliver(item, target))
                { best = target; distance = inventory.DistanceTo(target); expiry = candidate; }
            }
            return best;
        }

        private LogisticsItem PlatedDish(out GameObject destination)
        {
            RefreshWorld(); LogisticsItem best = null; destination = null; float distance = Single.MaxValue, expiry = Single.MaxValue;
            GameObject held = ActionObservations.Held(chef);
            foreach (LogisticsItem item in inventory.Items)
            {
                if (item.Supply || item.InUse || item.Item.GetComponent<ServerPlate>() == null || !Obtainable(item, "move_item")) continue;
                float candidate;
                GameObject window = ServingWindow(item.Item, out candidate);
                if (window == null) continue;
                // Do this before comparing walking costs: a ready plate in P2's
                // own hands never needs another pickup or a parking detour.
                if (System.Object.ReferenceEquals(item.Item, held)) { destination = window; return item; }
                float cost = item.Distance + inventory.DistanceTo(window);
                if (candidate < expiry || candidate == expiry && cost < distance)
                { best = item; destination = window; distance = cost; expiry = candidate; }
            }
            return best;
        }

        private bool CanTakeDish(GameObject item, GameObject destination)
        {
            RefreshWorld();
            float expiry;
            if (!DishExpiry(item, destination, out expiry)) return false;
            GameObject preferredWindow;
            LogisticsItem preferred = PlatedDish(out preferredWindow);
            if (preferred != null && !System.Object.ReferenceEquals(preferred.Item, item))
            {
                float earlier;
                if (DishExpiry(preferred.Item, preferredWindow, out earlier) && earlier < expiry) return false;
            }
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

        private IEnumerable<TaskStep> DeliverCompletedDishes()
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
