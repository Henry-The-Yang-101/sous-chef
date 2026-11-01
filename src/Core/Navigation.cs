using System;
using System.Collections.Generic;

namespace SousChef.Core
{
    public struct WalkCell : IEquatable<WalkCell>
    {
        public int X, Y, Z;
        public WalkCell(int x, int y, int z) { X = x; Y = y; Z = z; }
        public bool Equals(WalkCell other) { return X == other.X && Y == other.Y && Z == other.Z; }
        public override bool Equals(object other) { return other is WalkCell && Equals((WalkCell)other); }
        public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
    }
    public struct WalkEdge
    {
        public WalkCell Cell;
        public float Distance;
        public WalkEdge(WalkCell cell, float distance) { Cell = cell; Distance = distance; }
    }

    // One Dijkstra tree serves every destination. Costs are distance, not elapsed
    // time: environmental flow removes infeasible directed edges, never discounts
    // distance and silently trades a longer route for a faster one.
    public sealed class WalkSearch
    {
        private struct Pending { public WalkCell Cell; public float Cost; }
        private readonly List<Pending> heap = new List<Pending>();
        public readonly Dictionary<WalkCell, float> Distances = new Dictionary<WalkCell, float>();
        private readonly Dictionary<WalkCell, WalkCell> parents = new Dictionary<WalkCell, WalkCell>();
        public bool Complete { get; private set; }
        public WalkSearch(IEnumerable<WalkEdge> starts, Func<WalkCell, IEnumerable<WalkEdge>> expand,
            int maxNodes, Func<bool> budgetAvailable)
        {
            foreach (WalkEdge start in starts)
            {
                if (!Valid(start.Distance)) throw new ArgumentException("invalid_distance");
                float prior;
                if (Distances.TryGetValue(start.Cell, out prior) && prior <= start.Distance) continue;
                Distances[start.Cell] = start.Distance; Push(start.Cell, start.Distance);
            }
            while (heap.Count > 0)
            {
                if (Distances.Count > maxNodes || !budgetAvailable()) return;
                Pending next = Pop();
                if (Distances[next.Cell] < next.Cost) continue;
                foreach (WalkEdge edge in expand(next.Cell))
                {
                    if (!Valid(edge.Distance)) throw new ArgumentException("invalid_distance");
                    float cost = next.Cost + edge.Distance, old;
                    if (Distances.TryGetValue(edge.Cell, out old) && old <= cost) continue;
                    Distances[edge.Cell] = cost; parents[edge.Cell] = next.Cell; Push(edge.Cell, cost);
                }
            }
            Complete = true;
        }
        private static bool Valid(float cost) { return cost >= 0 && !Single.IsNaN(cost) && !Single.IsInfinity(cost); }
        public List<WalkCell> Route(WalkCell goal)
        {
            if (!Complete || !Distances.ContainsKey(goal)) return null;
            var path = new List<WalkCell>(); WalkCell current = goal, parent;
            while (true)
            {
                path.Add(current);
                if (!parents.TryGetValue(current, out parent)) break;
                current = parent;
            }
            path.Reverse(); return path;
        }
        private void Push(WalkCell cell, float cost)
        {
            var item = new Pending { Cell = cell, Cost = cost }; int at = heap.Count; heap.Add(item);
            while (at > 0)
            {
                int parent = (at - 1) / 2;
                if (heap[parent].Cost <= cost) break;
                heap[at] = heap[parent]; at = parent;
            }
            heap[at] = item;
        }
        private Pending Pop()
        {
            Pending result = heap[0], tail = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return result;
            int at = 0;
            while (at * 2 + 1 < heap.Count)
            {
                int child = at * 2 + 1;
                if (child + 1 < heap.Count && heap[child + 1].Cost < heap[child].Cost) child++;
                if (heap[child].Cost >= tail.Cost) break;
                heap[at] = heap[child]; at = child;
            }
            heap[at] = tail; return result;
        }
    }

    // Small explicit action lifecycle, independent of transport and Unity.
    public sealed class ChefActionState
    {
        public string State { get { return Destination == null ? "idle" : "navigating"; } }
        public string Destination { get; private set; }
        public string Reason { get; private set; }
        public long Revision { get; private set; }
        public int Owner { get; private set; }
        public bool Start(int owner, string destination, bool available)
        {
            if (!available || destination == null || Owner != 0) return false;
            Owner = owner; Destination = destination; Reason = null; Revision++; return true;
        }
        public void Stop(string reason)
        {
            Destination = null; Owner = 0; Reason = reason; Revision++;
        }
    }

    public static class WalkingSteering
    {
        // Solve |speed * input + drift| along the requested ground direction.
        // Reject crosswinds/counter-flow that cannot be overcome by walking.
        public static bool Direction(float dx, float dz, float windX, float windZ, float speed, out float x, out float z)
        {
            x = z = 0;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < .0001f || speed <= .01f) return false;
            dx /= length; dz /= length;
            float along = windX * dx + windZ * dz;
            float crossX = windX - along * dx, crossZ = windZ - along * dz;
            float remaining = speed * speed - crossX * crossX - crossZ * crossZ;
            if (remaining <= 0) return false;
            float forward = (float)Math.Sqrt(remaining);
            if (forward + along <= .1f) return false;
            x = (forward * dx - crossX) / speed; z = (forward * dz - crossZ) / speed;
            return true;
        }
    }

    // Nearby native grid selection alone is not proof of usable alignment.
    public sealed class ArrivalCheck
    {
        public const float PositionTolerance = .12f;
        public const float FacingCosine = .9961947f; // five degrees
        private double readySince = -1;
        public void Reset() { readySince = -1; }
        public bool Observe(double now, float distance, float facingDot, float speed, bool selected)
        {
            if (!(distance <= PositionTolerance && facingDot >= FacingCosine && speed <= .2f && selected))
            { Reset(); return false; }
            if (readySince < 0) readySince = now;
            return now - readySince >= .1;
        }
    }
}
