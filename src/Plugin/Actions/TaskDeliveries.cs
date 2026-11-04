using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class PendingDelivery { public GameObject Item; public int Player; }
        private readonly List<PendingDelivery> pendingDeliveries = new List<PendingDelivery>();
        private float nextDeliveryCheck;
        internal void DispatchDelivery(GameObject item, int player)
        {
            deliveredItems.Add(item); // In transit is already allocated to the human.
            pendingDeliveries.Add(new PendingDelivery { Item = item, Player = player });
        }
        private void ObserveDeliveries()
        {
            if (pendingDeliveries.Count == 0 || Time.realtimeSinceStartup < nextDeliveryCheck) return;
            nextDeliveryCheck = Time.realtimeSinceStartup + .2f;
            var players = new HashSet<int>();
            foreach (PendingDelivery delivery in pendingDeliveries) players.Add(delivery.Player);
            foreach (int player in players)
            {
                RefreshHuman(player); // One read-only route slice per player, not per shipment.
                for (int n = pendingDeliveries.Count - 1; n >= 0; n--)
                    if (pendingDeliveries[n].Player == player && HumanCanCollect(pendingDeliveries[n].Item, player, false))
                        pendingDeliveries.RemoveAt(n);
            }
        }
    }
}
