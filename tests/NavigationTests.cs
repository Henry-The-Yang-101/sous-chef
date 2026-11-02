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
        int expansions = 0;
        Func<WalkCell, IEnumerable<WalkEdge>> counted = node => { expansions++; return expand(node); };
        int remaining = 1;
        var sliced = new WalkSearch(new[] {new WalkEdge(a, 0)}, counted, 50, () => remaining-- > 0);
        check(expansions == 1 && !sliced.Complete, "first slice expands one node");
        for (int i = 0; i < 10 && !sliced.Complete; i++)
        { remaining = 1; sliced.Continue(() => remaining-- > 0); }
        var uninterrupted = plan();
        check(sliced.Complete && expansions == sliced.Settled.Count, "resumption does not re-expand the settled prefix");
        foreach (var pair in uninterrupted.Distances)
            check(sliced.Distances[pair.Key] == pair.Value, "sliced search preserves shortest-path costs");
        check(sliced.Route(goal).Count == uninterrupted.Route(goal).Count && sliced.Route(goal)[1].Equals(b), "sliced search preserves route reconstruction");
        sliced.Continue(() => true);
        check(expansions == sliced.Settled.Count, "completed search does no repeated expansion");
        var capped = new WalkSearch(new[] {new WalkEdge(a, 0)}, counted, 1, () => true);
        int beforeCap = expansions; capped.Continue(() => true);
        check(capped.NodeLimitReached && !capped.Complete && expansions == beforeCap, "continuation cannot bypass the node cap");
        int expandedChain = 0, allowance = 5;
        Func<WalkCell, IEnumerable<WalkEdge>> chain = node => {
            expandedChain++;
            return node.X < 99 ? new[] { new WalkEdge(new WalkCell(node.X + 1, 0, 0), 1) } : new WalkEdge[0];
        };
        var progressive = new WalkSearch(new[] {new WalkEdge(a, 0)}, chain, 1000, () => allowance-- > 0);
        int slices = 1;
        while (!progressive.Complete && slices < 30)
        { allowance = 5; progressive.Continue(() => allowance-- > 0); slices++; }
        check(progressive.Complete && slices == 20 && expandedChain == 100 && progressive.Distances[new WalkCell(99,0,0)] == 99,
            "twenty five-node slices fill the entire catalogue instead of repeating the first five nodes");
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
        // Two decks joined by two staircases. Every riser is within native step
        // height, although the complete deck separation is much larger.
        var stairs = new[] { new WalkCell(0, 0, 0), new WalkCell(1, 5, 0),
            new WalkCell(2, 10, 0), new WalkCell(3, 15, 0), new WalkCell(4, 18, 0),
            new WalkCell(1, 5, 2), new WalkCell(2, 10, 2), new WalkCell(3, 15, 2) };
        var links = new[] { new[] {0,1}, new[] {1,2}, new[] {2,3}, new[] {3,4},
            new[] {0,5}, new[] {5,6}, new[] {6,7}, new[] {7,4}, new[] {0,4} };
        bool closeNearStairs = false, closeFarStairs = false;
        Func<WalkCell, IEnumerable<WalkEdge>> staircase = delegate(WalkCell node)
        {
            var result = new List<WalkEdge>();
            for (int i = 0; i < links.Length; i++)
            {
                if ((i < 4 && closeNearStairs) || (i >= 4 && i < 8 && closeFarStairs)) continue;
                int first = links[i][0], second = links[i][1];
                if (!WalkHeight.Connected(stairs[first].Y / 10f, stairs[second].Y / 10f, .65f)) continue;
                if (node.Equals(stairs[first])) result.Add(new WalkEdge(stairs[second], i < 4 ? 1 : 2));
                if (node.Equals(stairs[second])) result.Add(new WalkEdge(stairs[first], i < 4 ? 1 : 2));
            }
            return result;
        };
        tree = new WalkSearch(new[] {new WalkEdge(stairs[0], 0)}, staircase, 100, delegate {return true;});
        check(tree.Route(stairs[4]).Count == 5 && tree.Distances[stairs[4]] == 4, "lower deck reaches upper by shortest stairs, never direct vertical jump");
        tree = new WalkSearch(new[] {new WalkEdge(stairs[2], 0)}, staircase, 100, delegate {return true;});
        check(tree.Route(stairs[0]) != null && tree.Route(stairs[4]) != null, "mid-stair start keeps both decks reachable");
        closeNearStairs = true;
        tree = new WalkSearch(new[] {new WalkEdge(stairs[4], 0)}, staircase, 100, delegate {return true;});
        check(tree.Route(stairs[0]).Count == 5 && tree.Distances[stairs[0]] == 8, "blocked near stairs selects far staircase downhill");
        closeFarStairs = true;
        tree = new WalkSearch(new[] {new WalkEdge(stairs[0], 0)}, staircase, 100, delegate {return true;});
        check(tree.Route(stairs[4]) == null, "both staircases blocked does not connect decks through air");
        check(!new WalkCell(2,0,2).Equals(new WalkCell(2,18,2)), "stacked floors retain separate graph identities");
        check(WalkHeight.WaypointReached(.05f, .5f, .04f, .65f), "native stepping height does not strand a horizontally reached waypoint");
        check(!WalkHeight.WaypointReached(.05f, 1.85f, .04f, .65f), "waypoint on another deck cannot be skipped");
        check(!WalkHeight.Connected(1.85f, 0, .65f), "unsupported deck drop rejected");
        check(!WalkHeight.Connected(0, Single.NaN, .65f), "invalid height rejected");
        check(WalkingCommand.Milliseconds(1f/60) == 17, "normal movement window rounds up to native walking interval");
        check(WalkingCommand.Milliseconds(.1f) == 100, "10 FPS movement window covers the full frame");
        check(WalkingCommand.Milliseconds(.25f) == 250, "slow movement window is never truncated to sixty milliseconds");
        check(WalkingCommand.Milliseconds(0) == 17, "zero render delta still permits a bounded native step");
        check(WalkingCommand.Milliseconds(.9f) == 900, "maximum movement interval remains bounded");
        foreach (float invalid in new[] { -.1f, .901f, Single.NaN, Single.PositiveInfinity })
            check(WalkingCommand.Milliseconds(invalid) == 0, "invalid or excessive movement interval fails closed");
        // The old flat-edge shortcut checked only the midpoint. Both ends and
        // midpoint can have floor while the chef's footprint clips a hole between.
        Func<float, bool> hole = delegate(float fraction) { return fraction < .20f || fraction > .30f; };
        check(hole(0) && hole(.5f) && hole(1), "hole fixture defeats endpoint and midpoint-only support checks");
        check(!WalkSegments.Check(.566f, hole), "shared edge sampler rejects unsupported footprint between endpoints");
        // Use a sample-width hole that intersects a .4-unit edge's first interior sample.
        Func<float, bool> widerHole = delegate(float fraction) { return fraction < .25f || fraction > .4f; };
        check(!WalkSegments.Check(.4f, widerHole), "footprint over a hole removes a graph edge before walking");
        check(WalkSegments.Check(.4f, delegate(float t) {return true;}), "continuous floor remains traversable");
        check(!WalkSegments.Check(.4f, delegate(float t) {return t < 1;}), "unsupported endpoint is checked");
        check(!WalkSegments.Check(Single.NaN, delegate(float t) {return true;}), "invalid segment cannot be certified");
        check(WalkSegments.Advance(0, 3, delegate(int i) {return i == 0;}, delegate(int i) {return false;}) == 0,
            "nearby waypoint is retained when cutting its corner would cross a hole");
        check(WalkSegments.Advance(0, 3, delegate(int i) {return i == 0;}, delegate(int i) {return true;}) == 1,
            "safe corner can advance without stopping");
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
