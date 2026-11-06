using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // A request has at most ten lanes, independent of its repeat count. Slots
    // within a lane implement quantity (e.g. A,A,B); a blocked lane consumes
    // no quota. Only a verified handoff consumes demand.
    internal sealed class PreparationSchedule
    {
        internal sealed class Lane
        {
            public PreparationRequest Order;
            public long Delivered, Limit;
            public int InFlight;
            public float RetryAt;
            public GameObject ParkedMeal;
            public bool WantsMore { get { return System.Object.ReferenceEquals(ParkedMeal, null) && Delivered + InFlight < Limit; } }
        }
        internal sealed class Batch
        {
            public Lane Lane;
            public GameObject Station, Container;
            public bool Mixing { get { return Lane.Order.Preparation == "mixed"; } }
            public bool Intact { get { return Container != null && Station != null &&
                System.Object.ReferenceEquals(ActionObservations.Receiver(Station), Container) &&
                !LogisticsInventory.Empty(Container) && !TaskNative.Burnt(Container); } }
        }
        internal readonly Lane[] Lanes;
        internal readonly List<Batch> Cooking = new List<Batch>();
        private int cursor, used;
        public PreparationSchedule(TaskRequest request)
        {
            Lanes = new Lane[request.Items.Count];
            for (int i = 0; i < Lanes.Length; i++)
            {
                var order = request.Items[i];
                Lanes[i] = new Lane { Order = order, Limit = request.Repetitions == 0 ? Int64.MaxValue : request.Repetitions * order.Quantity };
            }
        }
        public object Describe()
        {
            var rows = new List<object>();
            foreach (var lane in Lanes)
                rows.Add(Json.Object("ingredient", lane.Order.Selector, "preparation", lane.Order.Preparation,
                    "delivered", lane.Delivered, "processing", lane.InFlight,
                    "parkedHandoff", lane.ParkedMeal == null ? null : (object)lane.ParkedMeal.GetInstanceID()));
            return rows;
        }
        public bool Reserved(GameObject item)
        {
            foreach (var lane in Lanes) if (System.Object.ReferenceEquals(lane.ParkedMeal, item)) return true;
            return false;
        }
        public Lane At(int offset) { return Lanes[(cursor + offset) % Lanes.Length]; }
        public void Dispatch(Lane lane)
        {
            int index = Array.IndexOf(Lanes, lane);
            used = index == cursor ? used + 1 : 1;
            cursor = index;
            if (used >= lane.Order.Quantity) { cursor = (cursor + 1) % Lanes.Length; used = 0; }
        }
        public long Complete(Lane lane)
        {
            lane.Delivered++;
            long rounds = Int64.MaxValue;
            foreach (Lane entry in Lanes) rounds = Math.Min(rounds, entry.Delivered / entry.Order.Quantity);
            return rounds;
        }
        public void StartCooking(Lane lane, GameObject station)
        {
            Cooking.Add(new Batch { Lane = lane, Station = station, Container = ActionObservations.Receiver(station) });
            lane.InFlight++;
        }
        public void Release(Batch batch) { Cooking.Remove(batch); batch.Lane.InFlight--; }
    }
}
