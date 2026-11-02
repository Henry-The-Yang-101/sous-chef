using System;
using System.Collections.Generic;
using System.IO;

namespace SousChef.Core
{
    public sealed class WalkAdjacency
    { public WalkCell Cell; public WalkEdge[] Edges; }
    public sealed class WalkDelta
    {
        public long Generation;
        public readonly WalkTree Tree = new WalkTree();
    }
    public sealed class WalkJob : IWorkerJob
    {
        public long Generation;
        public WalkEdge[] Starts;
        public WalkAdjacency[] Added;
        public object Run(WorkerConnection c)
        {
            c.Writer.Write((byte)2); c.Writer.Write(Generation);
            WorkerWire.Edges(c.Writer, Starts); c.Writer.Write(Added.Length);
            foreach (var a in Added) { WorkerWire.Cell(c.Writer, a.Cell); WorkerWire.Edges(c.Writer, a.Edges); }
            c.Writer.Flush();
            var delta = new WalkDelta { Generation = c.Reader.ReadInt64() };
            bool complete = c.Reader.ReadBoolean(), limited = c.Reader.ReadBoolean(); float minimum = c.Reader.ReadSingle(); int discovered = WorkerWire.Count(c.Reader, 40000);
            int count = WorkerWire.Count(c.Reader, 20000);
            for (int i = 0; i < count; i++)
            { WalkCell cell = WorkerWire.Cell(c.Reader); float cost = c.Reader.ReadSingle(); bool parent = c.Reader.ReadBoolean(); delta.Tree.Import(cell, cost, parent, parent ? WorkerWire.Cell(c.Reader) : new WalkCell()); }
            delta.Tree.Finish(complete, limited, minimum, discovered); return delta;
        }
    }
    // Main-thread native adjacency production; only detached arrays cross IPC.
    // The worker's ordered Dijkstra frontier determines what can be certified.
    public sealed class RemoteWalk : IDisposable
    {
        private readonly WorkerMailbox mailbox;
        private readonly Queue<WalkCell> pending = new Queue<WalkCell>();
        private readonly HashSet<WalkCell> queued = new HashSet<WalkCell>();
        private bool capped;
        private readonly long generation;
        private readonly WalkEdge[] starts;
        public readonly WalkTree Tree = new WalkTree();
        public bool Failed { get { return mailbox.Failed || capped; } }
        public RemoteWalk(WorkerHost host, long version, WalkEdge[] seeds)
        {
            generation = version; starts = seeds; mailbox = new WorkerMailbox(host);
            Tree.Finish(false, false, 0, seeds.Length);
            foreach (var e in seeds) if (queued.Add(e.Cell)) pending.Enqueue(e.Cell);
        }
        public void Continue(Func<WalkCell, IEnumerable<WalkEdge>> expand, Func<bool> budget)
        {
            var delta = mailbox.Take() as WalkDelta;
            if (delta != null && delta.Generation == generation)
            {
                foreach (WalkCell cell in delta.Tree.Settled)
                { WalkCell parent; bool has = delta.Tree.Parent(cell, out parent); Tree.Import(cell, delta.Tree.Distances[cell], has, parent); }
                Tree.Finish(delta.Tree.Complete, delta.Tree.NodeLimitReached, delta.Tree.MinimumPendingDistance, delta.Tree.DiscoveredCount);
            }
            if (!mailbox.Idle || Tree.Complete || Tree.NodeLimitReached) return;
            var added = new List<WalkAdjacency>();
            // Bound speculative production and keep the same native query kernel.
            while (pending.Count > 0 && added.Count < 128 && budget())
            {
                WalkCell cell = pending.Dequeue(); var edges = new List<WalkEdge>(expand(cell)).ToArray();
                added.Add(new WalkAdjacency { Cell = cell, Edges = edges });
                foreach (var e in edges)
                    {
                        if (queued.Contains(e.Cell)) continue;
                        if (queued.Count >= 32000) { capped = true; break; }
                        queued.Add(e.Cell); pending.Enqueue(e.Cell);
                    }
            }
            if (added.Count > 0) mailbox.Submit(new WalkJob { Generation = generation, Starts = starts, Added = added.ToArray() });
        }
        public void Dispose() { mailbox.Dispose(); }
    }
}
