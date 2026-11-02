using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

// Closed-loop CPU replay: production planning/steering drives a dynamic capsule.
// GroundCast and the native tangent-space locomotion equations are modeled from
// the installed game. The contact solver is independent, simplified, and is NOT
// Unity PhysX. Captured contact poses and update-order variants test its limits.
internal sealed class StairSimulation : IDisposable
{
    private readonly List<RecordedBox> boxes;
    private readonly PlayerControls chef;
    private readonly GroundCast ground;
    private readonly CapsuleCollider shape;
    private readonly NavigationActions navigation;
    private Vector3 input, force;
    private float inputUntil, fixedRemainder, nativeRemainder;
    private readonly float contactFraction;
    private readonly bool staleGround;
    private readonly string scenario;
    private bool retracted;
    private readonly StreamWriter trace;
    public int Frames, SettlingFrames;

    public StairSimulation(Vector3 start, Vector3 velocity, float resolution, bool stale, string output, string scenarioName = "floor")
    {
        Time.realtimeSinceStartup = 1; Time.frameCount = 0;
        Time.deltaTime = Time.unscaledDeltaTime = 1f / 60;
        contactFraction = resolution; staleGround = stale; scenario = scenarioName;
        boxes = RecordedBox.Install("wizard-stair-route.json");
        if (scenario == "missing") RemoveStairs();
        chef = new GameObject("simulated Player 2").Add(new PlayerControls());
        chef.gameObject.layer = 8;
        chef.gameObject.Add(new PlayerIDProvider());
        shape = chef.gameObject.Add(new CapsuleCollider { radius = .4f, height = 2, center = Vector3.up });
        ground = chef.gameObject.Add(new GroundCast { Simulated = true });
        chef.Movement.RunSpeed = 6; chef.Movement.MaxSpeed = 18;
        chef.transform.position = start; chef.Motion.Velocity = velocity;
        navigation = new NavigationActions((v, ms) => { input = v; inputUntil = Time.realtimeSinceStartup + ms / 1000f; }, () => input = Vector3.zero, double.PositiveInfinity);
        UpdateGround();
        if (output != null) trace = new StreamWriter(output);
    }
    public void Dispose() { trace?.Dispose(); }
    private void RemoveStairs()
    {
        boxes.RemoveAll(b => b.Centre.x > 13 && b.Centre.x < 17 &&
            (b.gameObject.name.StartsWith("Step", StringComparison.Ordinal) || b.gameObject.name == "Wedge"));
    }
    private static Vector3 Cross(Vector3 a, Vector3 b)
    { return new Vector3(a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x); }
    private Vector3 AxisClosest(Vector3 point)
    {
        Vector3 p = chef.transform.position;
        return new Vector3(p.x, Math.Clamp(point.y, p.y + shape.radius, p.y + shape.height - shape.radius), p.z);
    }
    private void UpdateGround()
    {
        var hits = Physics.GroundSphere(chef.transform.position + Vector3.up, shape.radius, Vector3.down, 2);
        var hit = hits.Where(h => h.distance > 0 && h.normal.y >= .52992f).OrderBy(h => h.distance).FirstOrDefault();
        chef.GroundCollider = null; ground.Normal = Vector3.up; ground.Point = Vector3.zero; ground.Distance = 0;
        if (hit.collider == null) return;
        Vector3 offset = hit.point - AxisClosest(hit.point);
        float separation = Math.Max(0, offset.magnitude - shape.radius);
        if (separation > .3f) return;
        chef.GroundCollider = hit.collider; ground.Normal = hit.normal; ground.Point = hit.point;
        ground.Distance = offset.y < 0 ? separation : 0;
    }
    private void PhysicsStep(float dt)
    {
        chef.Motion.Velocity += force * dt; force = Vector3.zero;
        chef.transform.position += chef.Motion.Velocity * dt;
        // Resolve all contacts, including simultaneous tread and ramp contacts.
        // Partial correction covers the residual penetration seen in live traces.
        for (int pass = 0; pass < 3; pass++)
            foreach (RecordedBox box in boxes)
            {
                Vector3 p = chef.transform.position;
                Vector3 low = p + Vector3.up * shape.radius, high = p + Vector3.up * (shape.height - shape.radius);
                if (!box.CapsuleOverlap(low, high, shape.radius)) continue;
                Vector3 axis = box.CapsuleClosest(low, high), offset = axis - box.Closest(axis);
                float penetration = shape.radius - offset.magnitude;
                if (offset.magnitude < .000001f || penetration <= .001f) continue;
                Vector3 normal = offset.normalized;
                chef.transform.position += normal * ((penetration - .001f) * contactFraction);
                float inward = Vector3.Dot(chef.Motion.Velocity, normal);
                if (inward < 0) chef.Motion.Velocity -= normal * inward;
            }
    }
    private void NativeUpdate(float dt)
    {
        nativeRemainder += dt;
        Vector3 command = Time.realtimeSinceStartup < inputUntil ? input.normalized : Vector3.zero;
        while (nativeRemainder >= 1f / 60)
        {
            nativeRemainder -= 1f / 60;
            Vector3 normal = chef.GroundCollider == null ? Vector3.up : ground.Normal;
            float gravity = chef.GroundCollider != null && chef.GroundCollider.gameObject.layer == 28 ? 5 : 100;
            force -= normal * gravity;
            Vector3 xAxis = Cross(normal, Vector3.forward).normalized;
            Vector3 zAxis = Cross(xAxis, normal).normalized;
            chef.Motion.Velocity = xAxis * (command.x * 6) + zAxis * (command.z * 6) +
                normal * Vector3.Dot(normal, chef.Motion.Velocity);
        }
    }
    public string Run(float fps, bool jitter)
    {
        Vector3 original = chef.transform.position;
        bool contactCase = scenario == "contact";
        if (contactCase)
        {
            // Seed an already-running action with the captured contact state.
            // Prepare its route at the immediately resolved pose, then restore
            // the recorded penetration before the first controller decision.
            var probe = new WalkPhysics(); probe.Refresh(chef);
            if (!probe.Stand(original, out var supported, out var support)) return "fixture:no_support";
            chef.transform.position = supported; UpdateGround();
        }
        var target = new GameObject("Chocolate"); target.transform.position = new Vector3(13.2f, 0, 9.6f);
        // Warm the same physics graph that normal harness polling warms. No chef
        // movement or retries of a running action are hidden in this preparation.
        for (int i = 0; i < 300; i++)
        {
            navigation.PrepareStances(chef);
            var description = (Json.Record)navigation.Describe();
            if ((bool)description["catalogComplete"]) break;
        }
        string error = navigation.StartPosition(chef, 1, target, new Vector3(14.4f, 0, 9.6f));
        if (error != null) return "start:" + error;
        if (contactCase)
        {
            chef.transform.position = original;
            chef.Motion.Velocity = new Vector3(-3.646194f, -4.200619f, 4.765005f);
            navigation.Tick(chef);
            if (navigation.Action.State != "navigating" || input.magnitude != 0 ||
                (string)((Json.Record)navigation.Describe())["phase"] != "settling_ground") return "fixture:contact_not_retained";
        }
        while (navigation.Action.State == "navigating" && Frames < fps * 46)
        {
            float dt = (1f / fps) * (jitter ? (Frames % 3 == 0 ? 1.6f : .7f) : 1);
            Time.deltaTime = Time.unscaledDeltaTime = dt; Time.realtimeSinceStartup += dt; Time.frameCount++;
            if (scenario == "retract" && !retracted && Time.realtimeSinceStartup >= 2.5f)
            { RemoveStairs(); retracted = true; }
            fixedRemainder += dt;
            while (fixedRemainder >= .02f) { fixedRemainder -= .02f; PhysicsStep(.02f); }
            if (staleGround)
            {
                // Native movement can consume the preceding input and ground
                // sample before either observer updates in this render frame.
                NativeUpdate(dt); navigation.Tick(chef); UpdateGround();
            }
            else
            {
                UpdateGround(); navigation.Tick(chef); NativeUpdate(dt);
            }
            Frames++;
            var state = (Json.Record)navigation.Describe();
            if ((string)state["phase"] == "settling_ground") SettlingFrames++;
            trace?.WriteLine(Json.Encode(Json.Object("frame", Frames, "time", Time.realtimeSinceStartup,
                "position", SousChef.Plugin.Values.Vector(chef.transform.position), "velocity", SousChef.Plugin.Values.Vector(chef.Motion.Velocity),
                "ground", chef.GroundCollider == null ? null : chef.GroundCollider.gameObject.name,
                "groundDistance", ground.Distance, "input", SousChef.Plugin.Values.Vector(input), "navigation", state)));
            if (chef.transform.position.y < -4) { trace?.Dispose(); return "fell"; }
        }
        trace?.Dispose();
        return navigation.Action.Reason ?? "unfinished";
    }
}

internal static class Program
{
    private static bool RunCase(string output, float fps, bool stale, string scenario, float resolution = .8f)
    {
        var start = scenario == "contact" ? new Vector3(15.26166f, -1.058443f, 5.796684f) : new Vector3(21.896292f, -1.835624f, 2.915806f);
        using (var simulation = new StairSimulation(start, Vector3.zero, resolution, stale, output, scenario))
        {
            string result = simulation.Run(fps, true);
            bool negative = scenario == "missing" || scenario == "retract";
            bool passed = negative ? new[] {"start:destination_unreachable", "not_grounded", "no_safe_start", "destination_unreachable", "route_blocked"}.Contains(result) : result == "arrived";
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {scenario}: {result}; fps={fps} staleGround={stale} correction={resolution} frames={simulation.Frames} settling={simulation.SettlingFrames}");
            return passed;
        }
    }
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--suite")
        {
            string directory = args.Length > 1 ? args[1] : null;
            if (directory != null) Directory.CreateDirectory(directory);
            int cases = 0, failed = 0;
            foreach (float fps in new[] {20f, 30f, 60f, 120f})
                foreach (bool stale in new[] {false, true})
                {
                    string output = directory == null ? null : Path.Combine(directory, $"floor-{fps}-{stale}.ndjson");
                    if (!RunCase(output, fps, stale, "floor")) failed++;
                    cases++;
                }
            foreach (string scenario in new[] {"contact", "missing", "retract"})
            {
                string output = directory == null ? null : Path.Combine(directory, scenario + ".ndjson");
                if (!RunCase(output, 60, true, scenario)) failed++;
                cases++;
            }
            string slowOutput = directory == null ? null : Path.Combine(directory, "slow-contact.ndjson");
            if (!RunCase(slowOutput, 60, true, "contact", .2f)) failed++;
            cases++;
            Console.WriteLine($"{cases - failed}/{cases} headless scenarios passed (independent contact solver, not Unity PhysX)");
            return failed == 0 ? 0 : 1;
        }
        string path = args.Length > 0 ? args[0] : null;
        float rate = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 60;
        bool staleGround = args.Length > 2 && args[2] == "stale";
        string kind = args.Length > 3 ? args[3] : "floor";
        return RunCase(path, rate, staleGround, kind) ? 0 : 1;
    }
}
