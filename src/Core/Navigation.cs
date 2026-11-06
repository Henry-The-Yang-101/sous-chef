using System;
using System.Collections.Generic;

namespace SousChef.Core
{
    public static class WalkSegments
    {
        // Shared by graph edges and execution, including the endpoints. A single
        // support probe or midpoint alone cannot certify the whole walking segment.
        public static bool Check(float length, Func<float, bool> supported)
        {
            if (Single.IsNaN(length) || Single.IsInfinity(length) || length < 0) return false;
            int steps = Math.Max(1, (int)Math.Ceiling(length / .15f));
            for (int i = 0; i <= steps; i++) if (!supported((float)i / steps)) return false;
            return true;
        }
        public static int Advance(int index, int count, Func<int, bool> reached, Func<int, bool> clear)
        {
            // Being near a corner does not grant permission to cut across it.
            while (index < count - 1 && reached(index) && clear(index + 1)) index++;
            return index;
        }
    }
    // Vertical continuity uses the game's step limit, never the deck's total height.
    public static class WalkHeight
    {
        public static bool Connected(float from, float to, float step)
        { return step > 0 && Math.Abs(to - from) <= step + .025f; }
        public static bool WaypointReached(float dx, float dy, float dz, float step)
        { return dx * dx + dz * dz < .18f * .18f && Connected(0, dy, step); }
    }
    public static class WalkingCommand
    {
        public static int OwnershipMilliseconds(int movementMilliseconds)
        { return Math.Max(500, movementMilliseconds + 50); }
        // Optional continuation through one delayed Update plus a physics tick.
        // The caller must certify this entire motion window before using it.
        // Keep slow-frame commands unchanged and ordinary extensions <= 100 ms.
        public static int ContinuationMilliseconds(int frameMilliseconds)
        { return frameMilliseconds <= 0 ? 0 : Math.Max(frameMilliseconds, Math.Min(100, frameMilliseconds * 2 + 20)); }
        // The motion window is also enforced at the native input read. Keep a
        // separate, longer ownership lease so frame jitter can neutralize input
        // without abandoning the action. Long stalls still stop safely.
        public static int Milliseconds(float frameSeconds)
        {
            if (Single.IsNaN(frameSeconds) || Single.IsInfinity(frameSeconds) || frameSeconds < 0 || frameSeconds > .9f) return 0;
            return (int)Math.Ceiling(Math.Max(1f / 60, frameSeconds) * 1000f);
        }
    }
    public struct WalkCell : IEquatable<WalkCell>
    {
        public int X, Y, Z, SurfaceHistory, Portal;
        public WalkCell(int x, int y, int z, int surfaceHistory = 0, int portal = 0) { X = x; Y = y; Z = z; SurfaceHistory = surfaceHistory; Portal = portal; }
        public bool Equals(WalkCell other) { return X == other.X && Y == other.Y && Z == other.Z && SurfaceHistory == other.SurfaceHistory && Portal == other.Portal; }
        public override bool Equals(object other) { return other is WalkCell && Equals((WalkCell)other); }
        public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791) ^ (SurfaceHistory * 397) ^ Portal; } }
    }
    public struct WalkEdge
    {
        public WalkCell Cell;
        public float Distance;
        public WalkEdge(WalkCell cell, float distance) { Cell = cell; Distance = distance; }
    }

    public class WalkTree
    {
        public readonly Dictionary<WalkCell, float> Distances = new Dictionary<WalkCell, float>();
        public readonly HashSet<WalkCell> Settled = new HashSet<WalkCell>();
        protected readonly Dictionary<WalkCell, WalkCell> parents = new Dictionary<WalkCell, WalkCell>();
        public bool Complete { get; protected set; }
        public virtual bool NodeLimitReached { get; set; }
        public virtual float MinimumPendingDistance { get; set; }
        public int DiscoveredCount { get; set; }
        public bool Parent(WalkCell cell, out WalkCell parent) { return parents.TryGetValue(cell, out parent); }
        public void Import(WalkCell cell, float cost, bool hasParent, WalkCell parent)
        { Distances[cell] = cost; Settled.Add(cell); if (hasParent) parents[cell] = parent; }
        public void Finish(bool complete, bool limited, float minimum, int discovered)
        { Complete = complete; NodeLimitReached = limited; MinimumPendingDistance = minimum; DiscoveredCount = discovered; }
        public List<WalkCell> Route(WalkCell goal)
        {
            if (!Settled.Contains(goal)) return null;
            var path = new List<WalkCell>(); WalkCell current = goal, parent;
            while (true)
            {
                path.Add(current);
                if (!parents.TryGetValue(current, out parent)) break;
                current = parent;
            }
            path.Reverse(); return path;
        }
    }

    // One Dijkstra tree serves every destination. Costs are distance, not elapsed
    // time: environmental flow removes infeasible directed edges, never discounts
    // distance and silently trades a longer route for a faster one.
    public sealed class WalkSearch : WalkTree
    {
        private struct Pending { public WalkCell Cell; public float Cost, Priority; }
        private readonly List<Pending> heap = new List<Pending>();
        private readonly Func<WalkCell, IEnumerable<WalkEdge>> expand;
        private readonly Func<WalkCell, float> heuristic;
        private readonly int maxNodes;
        public override bool NodeLimitReached { get { return Distances.Count > maxNodes; } set { } }
        public override float MinimumPendingDistance { get { return heap.Count == 0 ? Single.PositiveInfinity : heap[0].Priority; } set { } }
        public WalkSearch(IEnumerable<WalkEdge> starts, Func<WalkCell, IEnumerable<WalkEdge>> expand,
            int maxNodes, Func<bool> budgetAvailable) : this(starts, expand, maxNodes)
        {
            Continue(budgetAvailable);
        }

        // An optional consistent lower bound focuses A* on one goal region.
        // Then MinimumPendingDistance certifies that region only; callers must
        // not reuse it as the all-destination Dijkstra frontier.
        public WalkSearch(IEnumerable<WalkEdge> starts, Func<WalkCell, IEnumerable<WalkEdge>> expand, int maxNodes,
            Func<WalkCell, float> heuristic = null)
        {
            this.expand = expand; this.maxNodes = maxNodes; this.heuristic = heuristic;
            foreach (WalkEdge start in starts)
            {
                if (!Valid(start.Distance)) throw new ArgumentException("invalid_distance");
                float prior;
                if (Distances.TryGetValue(start.Cell, out prior) && prior <= start.Distance) continue;
                Distances[start.Cell] = start.Distance; Push(start.Cell, start.Distance);
            }
        }
        // The caller must establish that the graph and starting costs have not
        // changed before resuming. A slice keeps its frontier and settled work;
        // it never restarts Dijkstra or publishes an unsettled route as proven.
        public void Continue(Func<bool> budgetAvailable, Action<WalkCell, float> onSettled = null)
        {
            while (heap.Count > 0)
            {
                if (NodeLimitReached || !budgetAvailable()) return;
                Pending next = heap[0];
                if (Distances[next.Cell] < next.Cost) { Pop(); continue; }
                // An unavailable adjacency list pauses BEFORE settling/popping.
                // Unknown outgoing edges must not certify any more distant node.
                IEnumerable<WalkEdge> outgoing = expand(next.Cell);
                if (outgoing == null) return;
                Pop(); Settled.Add(next.Cell);
                if (onSettled != null) onSettled(next.Cell, next.Cost);
                foreach (WalkEdge edge in outgoing)
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
        private void Push(WalkCell cell, float cost)
        {
            float estimate = heuristic == null ? 0 : heuristic(cell);
            if (!Valid(estimate)) throw new ArgumentException("invalid_heuristic");
            var item = new Pending { Cell = cell, Cost = cost, Priority = cost + estimate }; int at = heap.Count; heap.Add(item);
            while (at > 0)
            {
                int parent = (at - 1) / 2;
                if (!Before(item, heap[parent])) break;
                heap[at] = heap[parent]; at = parent;
            }
            heap[at] = item;
        }
        private static bool Before(Pending a, Pending b)
        { return a.Priority < b.Priority || a.Priority == b.Priority && a.Cost > b.Cost; }
        private Pending Pop()
        {
            Pending result = heap[0], tail = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return result;
            int at = 0;
            while (at * 2 + 1 < heap.Count)
            {
                int child = at * 2 + 1;
                if (child + 1 < heap.Count && Before(heap[child + 1], heap[child])) child++;
                if (!Before(heap[child], tail)) break;
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
        public const float PositionTolerance = .20f;
        public const float ReleaseTolerance = .28f;
        public const float FacingCosine = .9961947f; // five degrees
        private double readySince = -1;
        private bool settling;
        public void Reset() { readySince = -1; settling = false; }
        public bool ShouldSettle(float distance)
        {
            settling = distance <= (settling ? ReleaseTolerance : PositionTolerance);
            return settling;
        }
        public bool Observe(double now, float distance, float facingDot, float speed, bool selected)
        {
            if (!(ShouldSettle(distance) && facingDot >= FacingCosine && speed <= .2f && selected))
            { readySince = -1; return false; }
            if (readySince < 0) readySince = now;
            return now - readySince >= .1;
        }
    }
}
