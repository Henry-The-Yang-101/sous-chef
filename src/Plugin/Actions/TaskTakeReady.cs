using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterTakeReady()
        { Register("take_ready", delegate { return ActionObservations.Held(chef) == null && ProcessingStation(false) != null; }, TakeReady); }
        private GameObject ProcessingStation(bool includeMixing, bool readyOnly = false)
        {
            inventory.Refresh(chef); GameObject best = null; float score = Single.MaxValue;
            foreach (GameObject station in navigation.Targets())
            {
                if (!TaskNative.Equipment(station) || !Reachable(station)) continue;
                GameObject item = ActionObservations.Receiver(station);
                if (TaskNative.Burnt(item) || (TaskNative.State(item) == null && (!includeMixing || TaskNative.State(item, true) == null))) continue;
                if (LogisticsInventory.Empty(item) && ActionObservations.Container(station) != null) continue;
                if (readyOnly && !TaskNative.Ready(item)) continue;
                float cost = (TaskNative.Ready(item) ? 0 : 10000) + inventory.DistanceTo(station);
                if (cost < score) { best = station; score = cost; }
            }
            return best;
        }
        private bool CanCollect(GameObject recipient, GameObject source)
        {
            if (recipient == null || source == null || recipient.GetComponent<ServerIngredientContainer>() == null) return false;
            var transfer = GameObjectUtils.RequestInterface<IContainerTransferBehaviour>(source);
            return transfer != null && transfer.CanTransferToContainer(recipient.GetComponent<ServerIngredientContainer>());
        }
        private LogisticsItem ServingUtensil(GameObject source)
        {
            inventory.Refresh(chef); LogisticsItem best = null; float cost = Single.MaxValue;
            foreach (LogisticsItem candidate in inventory.Items)
            {
                if ((!candidate.CleanPlate && candidate.UtensilType == null) || !candidate.Empty || candidate.InUse ||
                    !CanCollect(LogisticsChecks.Payload(candidate), source) || LogisticsChecks.Collect(chef, candidate, candidate.CleanPlate ? "get_plate" : "move_item") != null) continue;
                float distance = candidate.Distance + (candidate.CleanPlate ? 0 : 1000);
                if (distance < cost) { best = candidate; cost = distance; }
            }
            return best;
        }
        private IEnumerable<TaskStep> RemoveReady(GameObject station, GameObject item)
        {
            // The wait observes the originally selected food/container. It never
            // takes uncooked food just because a walking action has arrived.
            yield return Until("waiting_until_food_ready", delegate {
                return station != null && station.activeInHierarchy && System.Object.ReferenceEquals(ActionObservations.Receiver(station), item) && TaskNative.Ready(item);
            });
            LogisticsItem plate = ServingUtensil(item);
            if (plate != null)
            {
                yield return Do("pick_up", plate.Source);
                yield return Do("collect_contents", station);
                foreach (TaskStep park in Park(ActionObservations.Held(chef))) yield return park;
            }
            else
            {
                yield return Do("pick_up", station);
                foreach (TaskStep park in Park(item)) yield return park;
            }
        }
        private IEnumerable<TaskStep> TakeReady()
        {
            GameObject station = null;
            yield return Until("waiting_for_accessible_heat", delegate { station = ProcessingStation(false); return station != null; });
            GameObject item = ActionObservations.Receiver(station);
            foreach (TaskStep step in RemoveReady(station, item)) yield return step;
        }
    }
}
