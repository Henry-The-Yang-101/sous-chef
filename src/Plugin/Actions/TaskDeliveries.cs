using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        private sealed class PendingDelivery { public GameObject Item; public int Player; public Func<bool> Intact; public Action<bool> Receipt; }
        private readonly List<PendingDelivery> pendingDeliveries = new List<PendingDelivery>();
        private float nextDeliveryCheck;
        internal void DispatchDelivery(GameObject item, int player, Func<bool> intact = null, Action<bool> receipt = null)
        {
            RecordDelivery(item); // In transit is already allocated to the human.
            pendingDeliveries.Add(new PendingDelivery { Item = item, Player = player, Intact = intact, Receipt = receipt });
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
                {
                    var delivery = pendingDeliveries[n];
                    if (delivery.Player != player) continue;
                    bool intact = delivery.Intact == null || delivery.Intact();
                    if (intact && !HumanCanCollect(delivery.Item, player, false)) continue;
                    pendingDeliveries.RemoveAt(n);
                    if (delivery.Receipt != null) delivery.Receipt(intact);
                }
            }
        }
    }
}
