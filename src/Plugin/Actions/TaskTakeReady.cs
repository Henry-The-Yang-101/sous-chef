using System;
using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterTakeReady()
        { Register("take_ready", delegate { return ProcessingStation(false) != null; }, TakeReady); }
        private GameObject ProcessingStation(bool includeMixing, bool readyOnly = false)
        {
            inventory.Refresh(chef); GameObject best = null; float score = Single.MaxValue;
            foreach (GameObject station in navigation.Targets())
            {
                if (!TaskNative.Equipment(station) || !Reachable(station)) continue;
                bool mixer = TaskNative.IsMixer(station);
                if (mixer && !includeMixing) continue;
                GameObject item = ActionObservations.Receiver(station);
                if (TaskNative.Burnt(item) || TaskNative.State(item, mixer) == null) continue;
                if (LogisticsInventory.Empty(item) && ActionObservations.Container(station) != null) continue;
                if (readyOnly && !TaskNative.Ready(item, mixer)) continue;
                float cost = (TaskNative.Ready(item, mixer) ? TaskNative.Remaining(item, mixer, true) * 100 : 100000) + inventory.DistanceTo(station);
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
        private LogisticsItem ServingUtensil(GameObject source, GameObject station, bool offHeat = false)
        {
            inventory.Refresh(chef); LogisticsItem best = null; float cost = Single.MaxValue;
            float margin = offHeat ? Single.MaxValue : TaskNative.Remaining(source, TaskNative.IsMixer(station), true);
            float stationDistance = inventory.DistanceTo(station);
            foreach (LogisticsItem candidate in inventory.Items)
            {
                if ((!candidate.CleanPlate && candidate.UtensilType == null) || !candidate.Empty || candidate.InUse ||
                    !CanCollect(LogisticsChecks.Payload(candidate), source) || LogisticsChecks.Collect(chef, candidate, candidate.CleanPlate ? "get_plate" : "move_item", true) != null ||
                    !LogisticsChecks.AcceptsPlacement(chef, LogisticsChecks.Payload(candidate), station)) continue;
                if (System.Object.ReferenceEquals(candidate.Item, ActionObservations.Held(chef))) return candidate;
                // Fetching a plate across stairs/another island can consume the
                // entire ready window. Prefer immediate utensil removal when a
                // conservative round trip would let this batch become ruined.
                float seconds = (candidate.Distance * 2 + stationDistance) / Math.Max(.1f, chef.Movement.RunSpeed) + 8;
                if (seconds >= margin) continue;
                float distance = candidate.Distance + (candidate.CleanPlate ? 0 : 1000);
                if (distance < cost) { best = candidate; cost = distance; }
            }
            return best;
        }
        private TaskStep GetServingUtensil(LogisticsItem utensil, GameObject food)
        {
            TaskStep pickup = Do("pick_up", utensil.Source);
            pickup.UseHeld = held => ServingRecipient(held, food);
            return pickup;
        }
        private bool ServingRecipient(GameObject recipient, GameObject food)
        { return recipient != null && LogisticsInventory.Empty(recipient) &&
            (recipient.GetComponent<ServerPlate>() != null || LogisticsInventory.UtensilType(recipient) != null) && CanCollect(recipient, food); }
        private IEnumerable<TaskStep> RemoveReady(GameObject station, GameObject item)
        {
            // The wait observes the originally selected food/container. It never
            // takes uncooked food just because a walking action has arrived.
            yield return Until("waiting_until_food_ready", delegate {
                return station != null && station.activeInHierarchy && System.Object.ReferenceEquals(ActionObservations.Receiver(station), item) && TaskNative.ReadyAt(station);
            });
            // Serve in place when the station accepts the actual utensil and
            // there is time to fetch it. If that proof stops holding before
            // the press, switch to rescuing the cookware instead of waiting.
            Func<bool> urgent = () => !TaskNative.ReadyAt(station) ||
                TaskNative.Remaining(item, TaskNative.IsMixer(station), true) < 5f;
            LogisticsItem serving = null;
            yield return Until("checking_serving_utensils", delegate {
                RefreshWorld();
                if (!botSearchComplete && !ServingRecipient(ActionObservations.Held(chef), item) && !urgent()) return false;
                serving = ServingUtensil(item, station); return true;
            });
            if (serving != null)
            {
                bool heldPlate = ServingRecipient(ActionObservations.Held(chef), item);
                Func<bool> unsafeToWait = () => child.TransferUnavailable || urgent();
                if (!heldPlate)
                {
                    TaskStep fetch = GetServingUtensil(serving, item);
                    fetch.ReplanOnBlock = true; fetch.AbandonBeforePress = unsafeToWait;
                    yield return fetch;
                }
                GameObject plateInHand = ActionObservations.Held(chef);
                if (ServingRecipient(plateInHand, item))
                {
                    TaskStep serve = Do("collect_contents", station);
                    serve.ReplanOnBlock = true; serve.AbandonBeforePress = unsafeToWait;
                    yield return serve;
                    foreach (TaskStep park in Park(plateInHand, serve.Succeeded ? serving.Holder : null)) yield return park;
                    if (serve.Succeeded) yield break;
                }
            }
            if (!System.Object.ReferenceEquals(ActionObservations.Receiver(station), item)) yield break;
            yield return Do("pick_up", station);
            foreach (TaskStep park in Park(item)) yield return park;
            // Off heat there is time to try the native transfer at the holder.
            GameObject holder = null; LogisticsItem plate = null;
            yield return Until("checking_serving_utensils", delegate {
                RefreshWorld();
                if (!botSearchComplete) return false;
                foreach (LogisticsItem candidate in inventory.Items)
                    if (System.Object.ReferenceEquals(candidate.Item, item)) { holder = candidate.Holder; break; }
                if (holder != null) plate = ServingUtensil(item, holder, true);
                return true;
            });
            if (plate != null)
            {
                yield return GetServingUtensil(plate, item);
                TaskStep serve = Do("collect_contents", holder);
                serve.ReplanOnBlock = true;
                serve.AbandonBeforePress = () => child.TransferUnavailable;
                yield return serve;
                foreach (TaskStep park in Park(ActionObservations.Held(chef))) yield return park;
            }
        }
        private IEnumerable<TaskStep> TakeReady()
        {
            while (MoreRequested)
            {
                GameObject station = null;
                // Observe all burners until one becomes ready. Binding to the nearest
                // unfinished pot could leave earlier batches burning on other stoves.
                yield return Until("waiting_for_accessible_heat", delegate { station = ProcessingStation(false, true); return station != null; });
                GameObject item = ActionObservations.Receiver(station);
                foreach (TaskStep step in RemoveReady(station, item)) yield return step;
                completed++;
            }
        }
    }
}
