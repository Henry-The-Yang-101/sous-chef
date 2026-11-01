using System;
using System.Collections.Generic;
using SousChef.Core;

internal static class NavigationTests
{
    public static int Run()
    {
        int checks = 0;
        Action<bool, string> check = delegate(bool ok, string message) { if (!ok) throw new Exception(message); checks++; };
        var a = new WalkCell(0, 0, 0); var b = new WalkCell(1, 0, 0);
        var c = new WalkCell(1, 0, 1); var goal = new WalkCell(2, 0, 0); var island = new WalkCell(5, 0, 5);
        bool wallClosed = false;
        Func<WalkCell, IEnumerable<WalkEdge>> expand = delegate(WalkCell node)
        {
            if (node.Equals(a)) return new[] { new WalkEdge(b, 1), new WalkEdge(c, 1.4f) };
            if (node.Equals(b) && !wallClosed) return new[] { new WalkEdge(goal, 1) };
            if (node.Equals(c) && !wallClosed) return new[] { new WalkEdge(goal, 1.4f) };
            return new WalkEdge[0];
        };
        Func<WalkSearch> plan = delegate { return new WalkSearch(new[] { new WalkEdge(a, 0) }, expand, 50, delegate { return true; }); };
        WalkSearch tree = plan();
        check(tree.Complete && tree.Distances[goal] == 2, "shortest route chosen for all targets");
        check(tree.Route(goal).Count == 3 && tree.Route(goal)[1].Equals(b), "route reconstruction");
        check(tree.Route(island) == null, "disconnected island omitted");
        wallClosed = true; tree = plan();
        check(!tree.Distances.ContainsKey(goal), "moving wall removes destination");
        wallClosed = false; tree = plan();
        check(tree.Distances.ContainsKey(goal), "opening wall restores destination");
        tree = new WalkSearch(new[] { new WalkEdge(a, 0) }, expand, 50, delegate { return false; });
        check(!tree.Complete && tree.Route(a) == null, "partial budget search is not authoritative");
        tree = new WalkSearch(new[] { new WalkEdge(a, 0) }, expand, 1, delegate { return true; });
        check(!tree.Complete && tree.Route(goal) == null, "node cap fails closed");
        int budget = 2;
        tree = new WalkSearch(new[] { new WalkEdge(a, 0) }, expand, 50, delegate { return budget-- > 0; });
        check(!tree.Complete && tree.Settled.Contains(b) && tree.Route(b).Count == 2, "settled routes survive an incomplete full-map search");
        check(!tree.Settled.Contains(goal) && tree.Route(goal) == null, "frontier guesses are never published as proven routes");
        check(tree.Distances[b] < tree.MinimumPendingDistance, "frontier bound certifies nearest known approach");
        Func<WalkCell, IEnumerable<WalkEdge>> fireDetour = delegate(WalkCell node)
        {
            if (node.Equals(a)) return new[] { new WalkEdge(b, 1), new WalkEdge(c, 1.4f) };
            // Fire removes the short branch, leaving the alternative open.
            if (node.Equals(c)) return new[] { new WalkEdge(goal, 1.4f) };
            return new WalkEdge[0];
        };
        tree = new WalkSearch(new[] { new WalkEdge(a, 0) }, fireDetour, 50, delegate { return true; });
        check(tree.Complete && Math.Abs(tree.Distances[goal] - 2.8f) < .001f && tree.Route(goal)[1].Equals(c), "a blocked branch preserves destinations via the alternate shortest route");
        // Directed edges represent an impassable counter-current.
        tree = new WalkSearch(new[] { new WalkEdge(goal, 0) }, expand, 50, delegate { return true; });
        check(!tree.Distances.ContainsKey(a), "one-way flow does not imply reverse reachability");
        float x, z;
        check(WalkingSteering.Direction(1, 0, 0, 2, 4, out x, out z) && z < 0 && Math.Abs(x*x+z*z-1) < .0001, "crosswind compensated at walking speed");
        check(!WalkingSteering.Direction(1, 0, -5, 0, 4, out x, out z), "opposing belt faster than chef rejected");
        check(!WalkingSteering.Direction(1, 0, 0, 5, 4, out x, out z), "uncounterable crosswind rejected");
        check(WalkingSteering.Direction(1, 0, 5, 0, 4, out x, out z), "following flow remains reachable");
        var arrival = new ArrivalCheck();
        check(!arrival.Observe(1, .5f, 1, 0, true), "nearby native selection cannot finish short of the stance");
        check(!arrival.Observe(2, .05f, .9f, 0, true), "station selected while facing away cannot finish");
        check(!arrival.Observe(3, .05f, 1, 2, true), "moving through a stance is not settled arrival");
        check(!arrival.Observe(4, .05f, 1, 0, false), "correct pose still requires native target selection");
        check(!arrival.Observe(5, .05f, 1, 0, true), "arrival requires stability across updates");
        check(arrival.Observe(5.11, .05f, 1, 0, true), "settled aligned native selection finishes");
        check(!arrival.Observe(6, .05f, 1, 0, false), "losing selection resets arrival latch");
        check(!arrival.Observe(6.2, .05f, 1, 0, true), "old stable time cannot survive target loss");
        check(!arrival.Observe(7, Single.NaN, 1, 0, true), "invalid observation cannot confirm arrival");
        arrival.Reset();
        check(!arrival.ShouldSettle(.24f), "wide release band cannot initiate premature arrival");
        check(arrival.ShouldSettle(.19f), "capture final approach before a full-speed reversal");
        check(arrival.ShouldSettle(.24f), "small braking drift does not restart walking");
        check(!arrival.Observe(8, .24f, 1, 0, true), "captured stance still needs stable native selection");
        check(arrival.Observe(8.11, .24f, 1, 0, true), "settled braking drift can finish without oscillation");
        check(!arrival.ShouldSettle(.29f), "larger displacement releases settling latch");
        check(!arrival.ShouldSettle(.24f), "released latch requires close approach again");
        arrival.ShouldSettle(.19f); arrival.Reset();
        check(!arrival.ShouldSettle(.24f), "stop or new action clears positional hysteresis");
        var action = new ChefActionState();
        check(!action.Start(1, "d1", false) && action.State == "idle", "unavailable target never starts");
        check(action.Start(1, "d1", true) && action.State == "navigating", "accepted action owns lifecycle");
        check(!action.Start(2, "d2", true), "concurrent action rejected");
        action.Stop("destination_unreachable");
        check(action.State == "idle" && action.Owner == 0 && action.Destination == null, "lost route resets action");
        action.Start(2, "d2", true); action.Stop("stopped");
        check(action.State == "idle" && action.Reason == "stopped", "explicit stop interrupts");
        check(ControlCommand.Parse("navigate_to r d24").Destination == "d24", "typed navigation command");
        check(ControlCommand.Parse("stop r").Operation == "stop", "stop command");
        check(ControlCommand.Parse("actions r").Operation == "actions", "catalog command");
        foreach (string bad in new[] { "navigate_to r d1 extra", "navigate_to r 1", "navigate_to r d-1", "navigate_to r d1\nstop", "stop r extra", "actions r extra" })
        {
            bool rejected = false; try { ControlCommand.Parse(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "malformed action rejected: " + bad);
        }
        return checks;
    }
}
