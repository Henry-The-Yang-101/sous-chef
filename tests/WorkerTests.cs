using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SousChef.Core;

internal static class WorkerTests
{
    public static int Run()
    {
        int checks = 0;
        Action<bool, string> check = (ok, message) => { if (!ok) throw new Exception(message); checks++; };
        var layout = new Json.FloatArrayLayout(new[] { "a", "b" }, new[] { 3, 2 });
        object value = Json.Object("unicode", "雪😀\ud800", "float", .1f, "nonfinite", Double.NaN,
            "numbers", new object[] { UInt64.MaxValue, Decimal.MaxValue, Int64.MinValue, true },
            "packed", layout.Capture(new[] { .1f, -0f, Single.PositiveInfinity, 2f, 3f }),
            "array", new[] { 1, -2, Int32.MaxValue });
        using (var memory = new MemoryStream())
        {
            var writer = new BinaryWriter(memory); WorkerWire.WriteValue(writer, value); writer.Flush(); memory.Position = 0;
            check(Json.Encode(value) == Json.Encode(WorkerWire.ReadValue(new BinaryReader(memory))), "binary IPC preserves all JSON types and UTF16 code units");
        }
        var a = new WalkCell(0,0,0); bool known = false;
        var deferred = new WalkSearch(new[] { new WalkEdge(a,0) }, cell => known ? new WalkEdge[0] : null, 100, () => true);
        check(deferred.Settled.Count == 0 && deferred.MinimumPendingDistance == 0 && !deferred.Complete, "missing adjacency cannot settle a node or certify distant routes");
        known = true; deferred.Continue(() => true); check(deferred.Complete && deferred.Settled.Count == 1, "missing adjacency resumes without losing frontier");
        string root = Directory.GetCurrentDirectory();
        using (var host = new WorkerHost(Path.Combine(root, ".tools/dotnet/dotnet"), Path.Combine(root, "artifacts/plugin/worker/Worker.dll")))
        {
            host.Start(); Wait(() => host.Ready, "Worker startup: " + host.Error); WorkerHost.Current = host;
            check(host.ProcessId != Process.GetCurrentProcess().Id, "planner is an actual separate process");
            check(WorkerHost.Encode(value) == Json.Encode(value), "real process encoding matches public wire JSON");
            Func<WalkCell, IEnumerable<WalkEdge>> expand = cell => {
                var result = new List<WalkEdge>();
                if (cell.X < 14) result.Add(new WalkEdge(new WalkCell(cell.X+1,0,cell.Z), 1 + cell.Z*.03f));
                if (cell.Z < 14) result.Add(new WalkEdge(new WalkCell(cell.X,0,cell.Z+1), 1));
                if (cell.X > 0) result.Add(new WalkEdge(new WalkCell(cell.X-1,0,cell.Z), 1));
                return result;
            };
            var expected = new WalkSearch(new[] { new WalkEdge(a,0) }, expand, 16000, () => true);
            int expansions = 0;
            using (var remote = new RemoteWalk(host, 7, new[] { new WalkEdge(a,0) }))
            {
                var clock = Stopwatch.StartNew();
                while (!remote.Tree.Complete && clock.Elapsed.TotalSeconds < 5)
                {
                    int allowance = 17;
                    remote.Continue(cell => { expansions++; return expand(cell); }, () => allowance-- > 0);
                    foreach (var cell in remote.Tree.Settled)
                        check(expected.Distances[cell] == remote.Tree.Distances[cell], "every published partial distance is certified");
                    Thread.Sleep(2);
                }
                check(remote.Tree.Complete && remote.Tree.Settled.SetEquals(expected.Settled), "worker fills the same complete destination graph");
                check(expansions == expected.Settled.Count, "batched adjacency production does not repeat native expansions");
                foreach (var cell in expected.Settled) check(remote.Tree.Route(cell).SequenceEqual(expected.Route(cell)), "worker preserves exact route tie ordering");
            }
            // An independent generation cannot retain old cells or parent edges.
            using (var reset = new RemoteWalk(host, 8, new[] { new WalkEdge(new WalkCell(99,0,0),0) }))
            {
                Wait(() => { reset.Continue(cell => new WalkEdge[0], () => true); return reset.Tree.Complete; }, "new generation completion");
                check(reset.Tree.Settled.Count == 1 && !reset.Tree.Settled.Contains(a), "new search has no stale generation data");
            }
            Wait(() => host.Flights != null, "throw worker queue startup");
            var input = new[] { 1f, 2f, 3f, 5f, 4f, -2f, 0f, -9.81f, 0f, .2f, .735f, .02f };
            var batch = new[] { input, (float[])input.Clone() };
            var task = host.Flights.Submit(batch); Wait(() => task.Complete, "flight batch completion");
            check(!task.Failed && task.Ends.SequenceEqual(FlightBatch.Ends(batch)), "worker trajectory endpoints preserve fixed-step scalar integration");
            using (var failed = new RemoteWalk(host, 9, new[] { new WalkEdge(a,0) }))
            {
                host.Dispose(); failed.Continue(expand, () => true); Wait(() => failed.Failed, "dead worker detected");
                check(!failed.Tree.Complete && failed.Tree.Settled.Count == 0, "worker loss cannot publish an unverified tree");
                check(WorkerHost.Encode(value) == Json.Encode(value), "encoding falls back with unchanged content after worker loss");
            }
        }
        return checks;
    }
    private static void Wait(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed.TotalSeconds > 8) throw new Exception(message); Thread.Sleep(5); }
    }
}
