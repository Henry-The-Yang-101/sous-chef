using System;
using System.Collections.Generic;
using System.Linq;
using SousChef.Core;
using SousChef.Plugin.Navigation;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private sealed class Floor : Collider
    {
        public Func<float,float,float?> Elevation;
        public Vector3 Normal = Vector3.up;
    }
    private sealed class Belt : MonoBehaviour, IMovingSurface
    {
        public Vector3 Velocity;
        public Vector3 CalculateVelocityAtPoint(Vector3 point, IMovingSurface previous) { return Velocity; }
    }
    private sealed class StationGrid : MonoBehaviour, IGridLocation
    {
        public GridManager Manager;
        public GridManager AccessGridManager { get { return Manager; } }
        public GridIndex GridIndex { get { return new GridIndex(); } }
    }
    private sealed class World
    {
        public readonly List<Collider> Colliders = new List<Collider>();
        public readonly PlayerControls Chef;
        public Vector3 LastInput;
        public int LastMilliseconds;
        public World()
        {
            Time.realtimeSinceStartup = 1; Time.deltaTime = Time.unscaledDeltaTime = 1f/60;
            var go = new GameObject("chef"); Chef = go.Add(new PlayerControls());
            go.Add(new CapsuleCollider()); go.Add(new GroundCast()); go.Add(new PlayerIDProvider());
            Physics.Nearby = (p,r) => Colliders.Where(c => c.enabled && c.gameObject.activeInHierarchy && Vector3.Distance(c.bounds.ClosestPoint(p),p) <= r).ToArray();
            Physics.Ray = (p,d,length,mask) =>
            {
                if (d.y > -.99f) return new RaycastHit[0];
                var hits = new List<RaycastHit>();
                foreach (Floor floor in Colliders.OfType<Floor>())
                {
                    float? y = floor.Elevation(p.x,p.z);
                    if (floor.enabled && y.HasValue && p.y >= y.Value && p.y-y.Value <= length)
                        hits.Add(new RaycastHit { collider=floor, point=new Vector3(p.x,y.Value,p.z),normal=floor.Normal,distance=p.y-y.Value });
                }
                return hits.ToArray();
            };
            Physics.Overlap = (a,b,r) => new Collider[0];
            Physics.Cast = (a,b,r,d,length) => new RaycastHit[0];
        }
        public Floor AddFloor(Func<float,float,float?> elevation, Vector3? normal = null)
        {
            var go = new GameObject("floor");
            var floor = go.Add(new Floor { Elevation=elevation, testBounds=new Bounds(Vector3.zero,new Vector3(200,2,200)) });
            if (normal.HasValue) floor.Normal=normal.Value;
            Colliders.Add(floor); if (Chef.GroundCollider == null) Chef.GroundCollider=floor;
            return floor;
        }
        public WalkPhysics Ready()
        {
            var physics = new WalkPhysics(); Check(physics.Refresh(Chef) == null,"fixture refresh succeeds"); return physics;
        }
        public GameObject AddStation()
        {
            var station=new GameObject("workstation"); station.Add(new ServerWorkstation());
            var manager=station.Add(new GridManager { Station=station });
            var grid=station.Add(new StationGrid { Manager=manager });
            var interact=station.Add(new ClientInteractable());
            Colliders.Add(station.Add(new BoxCollider { center=new Vector3(0,.5f,0),size=new Vector3(.8f,1,.8f) }));
            Chef.CurrentInteractionObjects.m_gridLocation=grid; Chef.CurrentInteractionObjects.m_interactable=interact;
            return station;
        }
        public NavigationActions Navigate(GameObject station)
        {
            var navigation=new NavigationActions((v,ms) => { LastInput=v;LastMilliseconds=ms; },() => {}); string id=null;
            for (int i=0; i<8; i++)
            {
                var catalog=(Json.Record)navigation.Available(Chef);
                foreach (Json.Record d in (List<object>)catalog["destinations"])
                    if ((int)d["objectInstanceId"] == station.GetInstanceID()) id=(string)d["id"];
                if ((bool)catalog["complete"]) break;
            }
            Check(id != null,"station is published by production discovery/search/matching");
            Check(navigation.Start(Chef,1,id) == null,"production action starts"); return navigation;
        }
    }
    private static void StepsAndRamps()
    {
        var w = new World(); w.AddFloor((x,z) => x < .5f ? 0 : (float?)null);
        Floor upper = w.AddFloor((x,z) => x >= .5f ? .5f : (float?)null);
        var physics = w.Ready(); Vector3 point; Collider support;
        Check(!physics.Stand(new Vector3(.4f,0,0),out point,out support),"a low non-Steppable lip cannot raise the planning body");
        Check(!physics.Segment(Vector3.zero,new Vector3(1,.5f,0)),"non-Steppable ascent is not executable");
        Check(!physics.Segment(new Vector3(1,.5f,0),Vector3.zero),"non-Steppable drop is not a walking shortcut");
        var nearLip = new WalkCell(0,0,0);
        Check(!physics.Neighbours(nearLip).Any(e => e.Cell.X == 1 && e.Cell.Z == 0),"non-Steppable footprint is omitted from cached neighbours");
        upper.gameObject.Add(new Steppable()); Time.realtimeSinceStartup += .21f;
        Check(physics.Refresh(w.Chef) == null,"step component reconciles");
        Check(physics.Segment(Vector3.zero,new Vector3(1,.5f,0)),"native Steppable ascent remains connected");
        Check(physics.Segment(new Vector3(1,.5f,0),Vector3.zero),"native Steppable descent remains connected");
        Check(physics.Neighbours(nearLip).Any(e => e.Cell.X == 1 && e.Cell.Z == 0),"adding Steppable invalidates the old cached rejection");
        w = new World(); w.AddFloor((x,z) => .5f*x,new Vector3(-.5f,1,0).normalized); physics=w.Ready();
        Check(physics.Segment(Vector3.zero,new Vector3(1,.5f,0)),"continuous ramp does not require Steppable");
    }
    private static void InteriorFlow()
    {
        var w = new World();
        w.AddFloor((x,z) => x < .1f || x > .2f ? 0 : (float?)null);
        Floor middle=w.AddFloor((x,z) => x >= .1f && x <= .2f ? 0 : (float?)null);
        Belt belt=middle.gameObject.Add(new Belt { Velocity=new Vector3(-5,0,0) });
        var physics=w.Ready(); var start=new WalkCell(0,0,0); var end=new WalkCell(1,0,0);
        Check(!physics.Neighbours(start).Any(e => e.Cell.Equals(end)),"interior opposing belt removes edge with calm endpoints");
        Check(!physics.Segment(Vector3.zero,new Vector3(.4f,0,0)),"execution agrees about interior belt");
        belt.Velocity=Vector3.zero; Time.realtimeSinceStartup += .02f; physics.Refresh(w.Chef);
        Check(physics.Neighbours(start).Any(e => e.Cell.Equals(end)),"live belt change restores cached geometry edge");
        belt.Velocity=new Vector3(-5,0,0); Time.realtimeSinceStartup += .02f; physics.Refresh(w.Chef);
        Check(!physics.Neighbours(start).Any(e => e.Cell.Equals(end)),"live belt change excludes edge without geometry edits");
        w=new World(); w.AddFloor((x,z) => 0);
        var wind=new GameObject("narrow wind"); wind.transform.position=new Vector3(.2f,.5f,0);
        wind.Add(new WindVolume { Velocity=new Vector3(-5,0,0) });
        var box=wind.Add(new BoxCollider { size=new Vector3(.05f,1,1),isTrigger=true }); w.Colliders.Add(box);
        physics=w.Ready();
        Check(!physics.Neighbours(start).Any(e => e.Cell.Equals(end)),"interior wind is checked at support samples");
        Check(!physics.Segment(Vector3.zero,new Vector3(1,0,0)),"wind near the start cannot hide outside a long segment's midpoint");
    }
    private static void MovingBoundary()
    {
        var w=new World(); w.AddFloor((x,z) => 0); var physics=w.Ready();
        var boundary=new WalkCell(145,0,0); var outside=new WalkCell(146,0,0);
        Check(!physics.Neighbours(boundary).Any(e => e.Cell.Equals(outside)),"initial search clips neighbour outside radius");
        w.Chef.transform.position=new Vector3(1,0,0); Time.realtimeSinceStartup += .02f; physics.Refresh(w.Chef);
        Check(physics.Neighbours(boundary).Any(e => e.Cell.Equals(outside)),"moving closer reveals cached boundary connection");
        w.Chef.transform.position=Vector3.zero; Time.realtimeSinceStartup += .02f; physics.Refresh(w.Chef);
        Check(!physics.Neighbours(boundary).Any(e => e.Cell.Equals(outside)),"moving away reapplies live radius to cached edge");
    }
    private static void ArrivalMotion()
    {
        var w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1); var station=w.AddStation(); var navigation=w.Navigate(station);
        w.Chef.WindReceiver.Velocity=new Vector3(.5f,0,0);
        for (int i=0; i<4; i++)
        {
            Time.realtimeSinceStartup += .06f; w.Chef.transform.position += new Vector3(.03f,0,0); navigation.Tick(w.Chef);
        }
        Check(navigation.Action.State == "navigating","wind drift past a stationary station cannot finish arrival");
        var details=(Json.Record)((Json.Record)navigation.Describe())["arrival"];
        Check((float)details["relativeSpeed"] > .4f,"arrival reports motion including wind drift");
        w.Chef.WindReceiver.Velocity=Vector3.zero;
        for (int i=0; i<4; i++) { Time.realtimeSinceStartup += .06f; navigation.Tick(w.Chef); }
        Check(navigation.Action.Reason == "arrived","settling after drift stops can finish");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1); station=w.AddStation(); navigation=w.Navigate(station);
        w.Chef.SurfaceMovable.Velocity=new Vector3(.5f,0,0);
        for (int i=0; i<4; i++)
        {
            Time.realtimeSinceStartup += .06f; var shift=new Vector3(.03f,0,0);
            w.Chef.transform.position += shift; station.transform.position += shift; navigation.Tick(w.Chef);
        }
        Check(navigation.Action.Reason == "arrived","chef and station sharing platform motion can finish relative arrival");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1); station=w.AddStation(); navigation=w.Navigate(station);
        for (int i=0; i<4; i++)
        {
            Vector3 previous=w.Chef.transform.position; Time.realtimeSinceStartup += .06f;
            station.transform.yaw += .03f; w.Chef.transform.position=station.transform.TransformPoint(new Vector3(0,0,-1));
            w.Chef.SurfaceMovable.Velocity=(w.Chef.transform.position-previous)/.06f; navigation.Tick(w.Chef);
        }
        Check(navigation.Action.Reason == "arrived","shared platform rotation preserves relative settling and the retained side");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1); station=w.AddStation(); navigation=w.Navigate(station);
        for (int i=0; i<4; i++)
        {
            Time.realtimeSinceStartup += .06f; w.Chef.transform.position += new Vector3(.03f,0,0); navigation.Tick(w.Chef);
        }
        Check(navigation.Action.State == "navigating","observed displacement catches external pushes even with zero motion velocity");
    }
    private static void ApproachFallback()
    {
        var w=new World(); bool bridge=true;
        Floor floor=w.AddFloor((x,z) =>
        {
            if (Math.Abs(x)>=2 || Math.Abs(z)>=2) return null;
            if (bridge || z<=-.6f || x>=-.3f || (x>=-1.4f && x<=-.6f && Math.Abs(z)<=.3f)) return 0;
            return null;
        });
        w.Chef.transform.position=new Vector3(-1,0,0); var station=w.AddStation(); var navigation=w.Navigate(station);
        string id=navigation.Action.Destination;
        bridge=false; floor.testBounds=new Bounds(Vector3.zero,new Vector3(201,2,201));
        w.Chef.transform.position=new Vector3(-1,0,-.85f); Time.realtimeSinceStartup += .21f;
        var catalog=(Json.Record)navigation.Available(w.Chef);
        var destination=((List<object>)catalog["destinations"]).Cast<Json.Record>().FirstOrDefault(d => (string)d["id"]==id);
        Check(destination != null,"reachable destination survives loss of retained side");
        float[] approach=(float[])destination["approachPosition"];
        Check(approach[0] > -.5f && approach[2] < -.5f,"matching falls back to the connected south side");
        Check(navigation.Action.State == "navigating","approach fallback preserves the active semantic target");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(-1,0,0); station=w.AddStation(); navigation=w.Navigate(station); id=navigation.Action.Destination;
        w.Chef.transform.position=new Vector3(-.3f,0,-.7f); Time.realtimeSinceStartup += .02f;
        catalog=(Json.Record)navigation.Available(w.Chef);
        destination=((List<object>)catalog["destinations"]).Cast<Json.Record>().First(d => (string)d["id"]==id);
        approach=(float[])destination["approachPosition"];
        Check(approach[0] < -.9f && Math.Abs(approach[2]) < .1f,"reachable retained side remains preferred while aligning");
    }
    private static void SlowFrames()
    {
        var w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.3f); var station=w.AddStation(); var navigation=w.Navigate(station);
        Time.deltaTime=Time.unscaledDeltaTime=.1f; Time.realtimeSinceStartup += .1f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.magnitude > .9f,"safe walking continues at ten FPS");
        Check(w.LastMilliseconds == 100,"movement callback receives the entire checked slow-frame window");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.3f); station=w.AddStation(); navigation=w.Navigate(station);
        var wall=new GameObject("wall beyond final stance").Add(new Collider());
        Physics.Cast=(a,b,r,d,length) => Math.Max(a.z,a.z+d.z*length) > -.95f ? new[] { new RaycastHit { collider=wall } } : new RaycastHit[0];
        Time.deltaTime=Time.unscaledDeltaTime=.1f; Time.realtimeSinceStartup += .1f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "next_step_unsafe","slow-frame projection catches overshoot missed by a sixty-millisecond check");
        Check(w.LastMilliseconds == 0,"unsafe slow-frame command is never emitted");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.3f); station=w.AddStation(); navigation=w.Navigate(station);
        Time.deltaTime=Time.unscaledDeltaTime=1; Time.realtimeSinceStartup += 1; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "unsafe_update_interval" && w.LastMilliseconds == 0,"excessive update stall emits no new movement");
    }
    private static void Main()
    {
        StepsAndRamps(); InteriorFlow(); MovingBoundary(); ArrivalMotion(); ApproachFallback(); SlowFrames();
        Console.WriteLine("PASS: " + checks + " navigator integration checks (analytical geometry; not PhysX)");
    }
}
