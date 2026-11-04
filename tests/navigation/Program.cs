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
    private sealed class CornerBelt : MonoBehaviour, IMovingSurface
    {
        public IMovingSurface Entering;
        public Vector3 CalculateVelocityAtPoint(Vector3 point, IMovingSurface previous)
        { return previous == Entering ? new Vector3(1,0,0) : new Vector3(-5,0,0); }
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
            Physics.Penetration = (a,p,ar,b,q,br) => false;
            Physics.Overlap = (a,b,r) => new Collider[0];
            Physics.Cast = (a,b,r,d,length) => new RaycastHit[0];
            Physics.GroundSphere = (p,r,d,length) =>
            {
                if (d.y > -.99f) return new RaycastHit[0];
                var hits = new List<RaycastHit>();
                foreach (Floor floor in Colliders.OfType<Floor>())
                {
                    if (!floor.enabled) continue;
                    Vector3 contact = p - floor.Normal*r, normal=floor.Normal;
                    float? y = floor.Elevation(contact.x,contact.z);
                    float centreY=float.NegativeInfinity;
                    if (y.HasValue) {contact.y=y.Value;centreY=contact.y+normal.y*r;}
                    else
                    {
                        // Rounded contact at an analytical surface's boundary.
                        // Sampling is fixture-only; production uses PhysX SphereCast.
                        for (int axis=0;axis<2;axis++)
                        {
                            float bestOffset=0, axisBest=float.NegativeInfinity;
                            Action<float> consider = offset =>
                            {
                                if (Math.Abs(offset)>r) return;
                                Vector3 q=p+(axis==0 ? Vector3.right : Vector3.forward)*offset;
                                float? edgeY=floor.Elevation(q.x,q.z); if(!edgeY.HasValue) return;
                                q.y=edgeY.Value;float rise=(float)Math.Sqrt(Math.Max(0,r*r-offset*offset));
                                if(q.y+rise>axisBest) {axisBest=q.y+rise;bestOffset=offset;}
                                if(q.y+rise<=centreY) return;
                                centreY=q.y+rise;contact=q;normal=(new Vector3(p.x,centreY,p.z)-q).normalized;
                            };
                            for(int i=-32;i<=32;i++) consider(r*i/32f);
                            // Converge the rounded edge contact instead of introducing
                            // discrete height jumps from the fixture's coarse samples.
                            float step=r/32;
                            for(int i=0;i<18;i++) {consider(bestOffset-step);consider(bestOffset+step);step*=.5f;}
                        }
                    }
                    float distance=p.y-centreY;
                    if (distance>0 && distance<=length) hits.Add(new RaycastHit {collider=floor,point=contact,normal=normal,distance=distance});
                }
                return hits.ToArray();
            };
        }
        public Floor AddFloor(Func<float,float,float?> elevation, Vector3? normal = null)
        {
            var go = new GameObject("floor"); go.layer = 9;
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
        Check(physics.Stand(new Vector3(.4f,0,0),out point,out support) && point.y > .4f,"sphere probe resolves a supported candidate; a route must separately prove the ascent");
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
        w = new World(); w.Chef.GetComponent<CapsuleCollider>().radius = .4f; w.AddFloor((x,z) => 1.2f*x,new Vector3(-1.2f,1,0).normalized); physics=w.Ready();
        Check(physics.Stand(Vector3.zero,out point,out support),"50-degree ramp supports a footprint spanning more than one step height");
        Check(physics.Segment(Vector3.zero,new Vector3(1,1.2f,0)),"steep continuous ramp remains traversable");
        w = new World();
        w.Chef.GetComponent<CapsuleCollider>().radius = .4f;
        // Flat treads joined by a 50-degree wedge, as in the installed festive stairs.
        w.AddFloor((x,z) => x <= 0 ? 0 : (float?)null);
        w.AddFloor((x,z) => x > 0 && x < .4f ? 1.2f*x : (float?)null,new Vector3(-1.2f,1,0).normalized);
        w.AddFloor((x,z) => x >= .4f ? .48f : (float?)null);
        physics=w.Ready();
        Check(physics.Segment(new Vector3(-.6f,0,0),new Vector3(1,.48f,0)),"footprint follows the wedge between flat treads without demanding Steppable");
        w = new World(); w.Chef.GetComponent<CapsuleCollider>().radius = .4f;
        // Cross-section measured in s_wizard_school_3_4: the wedge begins
        // 0.17 above the lower tread. Rounded native contact bridges this lip.
        w.AddFloor((x,z) => x <= .345f ? 0 : (float?)null);
        w.AddFloor((x,z) => x >= .229111f && x < .515248f ? .170646f+1.191754f*(x-.229111f) : (float?)null,new Vector3(-1.191754f,1,0).normalized);
        w.AddFloor((x,z) => x >= .515248f ? .502f : (float?)null);
        physics=w.Ready();
        Check(physics.Segment(new Vector3(-.6f,0,0),new Vector3(1,.502f,0)),"native-sized sphere contact traverses the captured wizard stair wedge lip");
        Check(physics.Segment(new Vector3(1,.502f,0),new Vector3(-.6f,0,0)),"captured stair wedge also connects on descent");
        w = new World(); w.Chef.GetComponent<CapsuleCollider>().radius = .4f;
        w.AddFloor((x,z) => x <= 0 || x >= .17f ? 0 : (float?)null); physics=w.Ready();
        Check(physics.Segment(new Vector3(-.6f,0,0),new Vector3(.8f,0,0)),"native capsule support spans a narrow tread seam without requiring every footprint ray to hit");
        w = new World(); w.Chef.GetComponent<CapsuleCollider>().radius = .4f;
        w.AddFloor((x,z) => x <= 0 || x >= 1 ? 0 : (float?)null); physics=w.Ready();
        Check(!physics.Segment(new Vector3(-.6f,0,0),new Vector3(1.6f,0,0)),"a gap wider than native support remains non-walkable");
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

        w=new World(); var incoming=w.AddFloor((x,z) => x < .1f ? 0 : (float?)null);
        var entering=incoming.gameObject.Add(new Belt {Velocity=new Vector3(1,0,0)});
        var corner=w.AddFloor((x,z) => x >= .1f ? 0 : (float?)null);
        corner.gameObject.Add(new CornerBelt {Entering=entering}); physics=w.Ready();
        Check(physics.Segment(Vector3.zero,new Vector3(.4f,0,0)),"corner belt receives the previous surface for every sample after entering");
        var cornerCell=physics.Neighbours(start).First(e=>e.Cell.X==1 && e.Cell.Z==0).Cell;
        Check(cornerCell.SurfaceHistory!=0,"cached edges retain entering conveyor as graph state");
        Check(!cornerCell.Equals(end),"same position with a different entering belt is a distinct flow state");
        Check(physics.Neighbours(cornerCell).Any(e=>e.Cell.X==2 && e.Cell.Z==0),"conveyor history persists across multiple graph edges");
        Check(!physics.Neighbours(end).Any(e=>e.Cell.X==2 && e.Cell.Z==0),"cached geometry does not reuse another arrival's flow permission");
        int history=0;
        Check(physics.Segment(Vector3.zero,new Vector3(.4f,0,0),ref history) &&
            physics.Segment(new Vector3(.4f,0,0),new Vector3(.8f,0,0),ref history),"remaining route checks carry conveyor history across segment boundaries");
        w.Chef.GroundCollider=corner; w.Chef.transform.position=new Vector3(.15f,0,0); w.Chef.SurfaceMovable.SetPrevious(entering); physics.Refresh(w.Chef);
        Check(physics.Segment(w.Chef.transform.position,new Vector3(.4f,0,0)),"execution connector retains the chef's native previous belt after crossing a seam");
    }
    private static void DirectedConveyors()
    {
        var w=new World(); var floor=w.AddFloor((x,z)=>0);
        var belt=floor.gameObject.Add(new Travelator {Velocity=new Vector3(3,0,0)});
        var physics=w.Ready();var start=new WalkCell(0,0,0);
        Check(physics.Segment(Vector3.zero,new Vector3(.4f,0,0)),"native belt permits downstream travel");
        Check(!physics.Segment(new Vector3(.4f,0,0),Vector3.zero),"native belt rejects upstream shortcut even when walking could overpower it");
        Check(!physics.Neighbours(start).Any(e=>e.Cell.X<0),"cached graph edges respect native conveyor direction");
        Check(physics.Segment(Vector3.zero,new Vector3(0,0,.4f)),"native belt permits compensated sideways exit");
        belt.Velocity=new Vector3(-3,0,0);physics.Refresh(w.Chef);
        Check(!physics.Neighbours(start).Any(e=>e.Cell.X>0) && physics.Neighbours(start).Any(e=>e.Cell.X<0),
            "live conveyor reversal changes cached directed edges");
        belt.enabled=false;physics.Refresh(w.Chef);
        Check(physics.Neighbours(start).Any(e=>e.Cell.X>0) && physics.Neighbours(start).Any(e=>e.Cell.X<0),
            "disabled conveyor restores both walking directions");
        belt.enabled=true;belt.Velocity=new Vector3(3,0,0);belt.transform.yaw=-(float)Math.PI/2;physics.Refresh(w.Chef);
        Check(!physics.Segment(Vector3.zero,new Vector3(0,0,-.4f)),"world-space conveyor direction supports rotated belts");
        var station=w.AddStation();station.transform.position=new Vector3(1.2f,0,0);
        Vector3 stance;var collider=station.GetComponent<BoxCollider>();
        Check(!physics.Approach(Vector3.zero,collider,out stance),"station approach on a running belt cannot promise settled arrival");
        belt.enabled=false;physics.Refresh(w.Chef);
        Check(physics.Approach(Vector3.zero,collider,out stance),"station approach on a stopped belt can settle");
        w=new World();floor=w.AddFloor((x,z)=>x<=0 ? 0 : (float?)null);
        floor.gameObject.Add(new Travelator {Velocity=new Vector3(-3,0,0)});physics=w.Ready();Collider support;
        Check(!physics.Stand(new Vector3(.2f,0,0),out stance,out support),"rounded conveyor lip cannot become a shortcut lane");
        Check(physics.Stand(new Vector3(-.5f,0,0),out stance,out support),"conveyor top remains supported for checked turns");
        Check(!physics.Stand(new Vector3(-.1f,0,0),out stance,out support),"padded graph keeps turns away from the conveyor's outside edge");
        w.Chef.transform.position=new Vector3(-.1f,0,0);physics.Refresh(w.Chef);
        Check(physics.Segment(w.Chef.transform.position,new Vector3(-.5f,0,0)),"native contact connector can recover to the conveyor lane without the extra planning margin");
        w=new World();floor=w.AddFloor((x,z)=>Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        belt=floor.gameObject.Add(new Travelator {Velocity=new Vector3(1,0,0),enabled=false});
        station=w.AddStation();station.transform.position=new Vector3(1.2f,0,1.2f);var navigation=w.Navigate(station);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(NavigationActions).GetField("route",flags).SetValue(navigation,new List<Vector3> {
            new Vector3(.4f,0,0),new Vector3(.4f,0,.4f),new Vector3(.8f,0,.4f)});
        typeof(NavigationActions).GetField("waypoint",flags).SetValue(navigation,0);belt.enabled=true;
        object[] arguments={Vector3.zero,Vector3.zero};
        Check((bool)typeof(NavigationActions).GetMethod("TryWalkingGoal",flags).Invoke(navigation,arguments) &&
            ((Vector3)arguments[1]).Equals(new Vector3(.4f,0,0)),"conveyor steering preserves the next turn instead of aiming past it");
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
    private static void EmptyHandsBin()
    {
        var w=new World();w.AddFloor((x,z)=>Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1);
        var bin=new GameObject("Bin 1");bin.Add(new RubbishBin());bin.Add(new AttachStation());
        var manager=bin.Add(new GridManager {Station=bin});var grid=bin.Add(new StationGrid {Manager=manager});
        w.Colliders.Add(bin.Add(new BoxCollider {center=new Vector3(0,.5f,0),size=new Vector3(.8f,1,.8f)}));
        w.Chef.CurrentInteractionObjects.m_gridLocation=grid;
        var navigation=w.Navigate(bin);
        Check(navigation.Action.State=="navigating","a bin is navigable with empty hands and no available discard action");
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
            // The disconnected island needs a gap wider than the native capsule.
            if (bridge || z<=-.6f || x>=-.1f || (x>=-1.4f && x<=-.7f && Math.Abs(z)<=.1f)) return 0;
            return null;
        });
        w.Chef.transform.position=new Vector3(-1,0,0); var station=w.AddStation(); var navigation=w.Navigate(station);
        string id=navigation.Action.Destination;
        bridge=false; floor.testBounds=new Bounds(Vector3.zero,new Vector3(201,2,201));
        w.Chef.transform.position=new Vector3(-1,0,-.85f); Time.realtimeSinceStartup += .21f;
        navigation.Tick(w.Chef);
        var catalog=(Json.Record)navigation.Available(w.Chef);
        var destination=((List<object>)catalog["destinations"]).Cast<Json.Record>().FirstOrDefault(d => (string)d["id"]==id);
        Check(destination != null,"reachable destination survives loss of retained side");
        float[] approach=(float[])destination["approachPosition"];
        Check(approach[0] > -.5f && approach[2] < -.5f,"matching falls back to the connected south side");
        Check(navigation.Action.State == "navigating","approach fallback preserves the active semantic target");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(-1,0,0); station=w.AddStation(); navigation=w.Navigate(station); id=navigation.Action.Destination;
        w.Chef.transform.position=new Vector3(-.3f,0,-.7f); Time.realtimeSinceStartup += .02f;
        navigation.Tick(w.Chef);
        catalog=(Json.Record)navigation.Available(w.Chef);
        destination=((List<object>)catalog["destinations"]).Cast<Json.Record>().First(d => (string)d["id"]==id);
        approach=(float[])destination["approachPosition"];
        Check(approach[0] < -.9f && Math.Abs(approach[2]) < .1f,"reachable retained side remains preferred while aligning");
    }
    private static void SlowFrames()
    {
        var cruise = new World(); cruise.AddFloor((x,z) => Math.Abs(x)<4 && Math.Abs(z)<4 ? 0 : (float?)null);
        cruise.Chef.transform.position = new Vector3(0,0,-2.3f);
        var cruiseNavigation = cruise.Navigate(cruise.AddStation());
        Time.realtimeSinceStartup += .02f; cruiseNavigation.Tick(cruise.Chef);
        Check(cruise.LastMilliseconds == 54 && cruise.LastInput.magnitude > .9f,
            "open supported travel certifies a longer window through ordinary native read jitter");
        cruise = new World(); cruise.AddFloor((x,z) => Math.Abs(x)<4 && Math.Abs(z)<4 ? 0 : (float?)null);
        cruise.Chef.transform.position = new Vector3(0,0,-1.3f);
        cruiseNavigation = cruise.Navigate(cruise.AddStation());
        Time.realtimeSinceStartup += .02f; cruiseNavigation.Tick(cruise.Chef);
        Check(cruise.LastMilliseconds == 17 && cruise.LastInput.magnitude > .9f,
            "final approach keeps the original movement and braking window");
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
        Check(navigation.Action.State == "navigating" && Math.Abs(w.LastInput.x) > .1f,
            "unsafe direct heading is replaced by a checked local turn");
        Check(w.LastMilliseconds == 100 && w.Chef.transform.position.z + w.LastInput.z * 4 * .1f <= -.95f,
            "the replacement heading clears the wall for the full slow-frame input window");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.3f); station=w.AddStation(); navigation=w.Navigate(station);
        wall=new GameObject("all next steps blocked").Add(new Collider());
        Physics.Cast=(a,b,r,d,length) =>
        {
            Vector3 end=a+d*length-new Vector3(0,0,-1.3f); end.y=0;
            return end.magnitude > .35f ? new[] {new RaycastHit {collider=wall}} : new RaycastHit[0];
        };
        Time.deltaTime=Time.unscaledDeltaTime=.1f; Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "next_step_unsafe" && w.LastMilliseconds == 0,
            "local direction search still stops when every full movement window is obstructed");

        w=new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.3f); station=w.AddStation(); navigation=w.Navigate(station);
        Time.deltaTime=Time.unscaledDeltaTime=1; Time.realtimeSinceStartup += 1; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "unsafe_update_interval" && w.LastMilliseconds == 0,"excessive update stall emits no new movement");
    }
    private static void CapturedStairCorner()
    {
        var w = new World();
        var shape = w.Chef.GetComponent<CapsuleCollider>(); shape.radius = .4f; shape.height = 2; shape.center = Vector3.up;
        var boxes = RecordedBox.Install(); w.Chef.GroundCollider = boxes[0];
        w.Chef.transform.position = new Vector3(16.765953f, -1.837692f, 4.441718f);
        var physics = w.Ready(); Vector3 point; Collider ground;
        Check(!physics.Stand(new Vector3(16.4f,-1.74191f,4.4f), out point, out ground), "recorded sphere-supported side contact is excluded from the ramp interior");
        Check(!physics.Stand(new Vector3(16.426f,-1.8356f,4.4f), out point, out ground), "recorded corner contains a body-blocked strip below the native slope cutoff");
        Check(!physics.Segment(w.Chef.transform.position,new Vector3(16.4f,-1.74191f,4.4f)), "planner rejects captured corner shortcut instead of bridging its blocked strip");
        Check(physics.Segment(new Vector3(15,-1.835624f,3.9f),new Vector3(15,-1.505f,4.9f)), "same captured wedge remains traversable through its front");
    }
    private static void CapturedStairContact()
    {
        var w = new World();
        var shape = w.Chef.GetComponent<CapsuleCollider>(); shape.radius = .4f; shape.height = 2; shape.center = Vector3.up;
        var boxes = RecordedBox.Install("wizard-stair-contact.json"); w.Chef.GroundCollider = boxes[0];
        w.Chef.transform.position = new Vector3(15.3860645f, -.993433f, 5.7824163f);
        var physics = w.Ready(); Vector3 support; Collider ground;
        Check(physics.Stand(w.Chef.transform.position, out support, out ground), "mid-stair capture has a clear supported body pose");
        Check(!physics.Starts().Any(), "native initial penetration makes every ordinary start sweep fail");
        Check(physics.NeedsGroundSettling(), "current capsule penetration identifies native upward contact recovery despite stale ground data");
        w.Chef.transform.position = new Vector3(15.26166f, -1.058443f, 5.796684f);
        Check(physics.NeedsGroundSettling(), "simultaneous tread and wedge penetrations resolve together under neutral input");
        w.Chef.transform.position = support;
        Check(!physics.NeedsGroundSettling() && physics.Starts().Any(), "native contact resolution makes the same route start usable again");
        w.Chef.transform.position = new Vector3(15.3860645f, -.993433f, 5.7824163f);
        var overlap = Physics.Overlap;
        var wall = new GameObject("unrelated wall").Add(new Collider());
        Physics.Overlap = (a,b,r) => a.y < -.55f ? overlap(a,b,r).Concat(new[] {wall}).ToArray() : overlap(a,b,r);
        Check(!physics.NeedsGroundSettling(), "an unrelated blocking overlap cannot be waved through as stair recovery");
    }
    private static void GroundSettling()
    {
        var w = new World(); var floor = w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position = new Vector3(0,0,-1.3f); var station = w.AddStation(); var navigation = w.Navigate(station);
        long revision = navigation.Action.Revision;
        w.Chef.GroundCollider = null; w.Chef.transform.position += Vector3.up * .35f;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.magnitude == 0 && w.LastMilliseconds > 0, "native stair hop keeps the action with neutral input above a checked landing");
        navigation.Available(w.Chef);
        Check(navigation.Action.State == "navigating" && navigation.Action.Revision == revision,
            "a concurrent harness catalogue read cannot cancel a recovering stair hop");
        w.Chef.GroundCollider = floor;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.magnitude == 0,
            "stale native ground contact cannot resume walking above the independent contact range");
        w.Chef.transform.position -= Vector3.up * .15f;
        w.Chef.GroundCollider = floor; w.Chef.GetComponent<GroundCast>().Distance = .24f;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.magnitude > .9f, "valid native proximity contact permits a checked route instead of repeatedly braking off the stair");
        w.Chef.GetComponent<GroundCast>().Distance = 0; w.Chef.transform.position -= Vector3.up * .2f;
        Time.realtimeSinceStartup += .1f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && navigation.Action.Revision == revision && w.LastInput.magnitude > .9f, "landing resumes the same request without requiring another Run click");
        w.Chef.GroundCollider = null; w.Chef.transform.position += Vector3.up * .9f;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.magnitude == 0, "native landing probe covers an airborne gap larger than the step-up limit");
        Time.realtimeSinceStartup += 2.01f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "ground_contact_timeout", "a landing that never regains native ground contact terminates within the stall deadline");

        w = new World(); floor = w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position = new Vector3(0,0,-1.3f); station = w.AddStation(); navigation = w.Navigate(station);
        floor.enabled = false; w.Chef.GroundCollider = null; w.Chef.transform.position += Vector3.up * .35f;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "not_grounded", "missing floor is not treated as a harmless stair hop");
        w = new World(); w.AddFloor((x,z) => Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        w.Chef.transform.position = new Vector3(0,0,-1.3f); station = w.AddStation(); navigation = w.Navigate(station);
        w.Chef.GroundCollider = null; w.Chef.transform.position += Vector3.up * 2;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason == "not_grounded", "a fall beyond native ground-probe range is not retained as stair settling");
    }
    private static void ShallowLandingContact()
    {
        var w = new World(); var floor = w.AddFloor((x,z) => 0);
        w.Chef.transform.position = new Vector3(0,-.03f,0);
        var physics = w.Ready();
        w.Chef.GroundCollider = null;
        Check(physics.NeedsGroundSettling(), "clear landing within the capsule skin waits for a stale null GroundCast to refresh");
        w.Chef.GroundCollider = floor;
        w.Chef.transform.position = new Vector3(0,-.07f,0);
        physics = w.Ready();
        Physics.Overlap = (a,b,r) => a.y-r < 0 ? new Collider[] {floor} : new Collider[0];
        Physics.Penetration = (a,p,ar,b,q,br) =>
        { Physics.PenetrationDirection = Vector3.forward; Physics.PenetrationDistance = .03f; return true; };
        Check(physics.NeedsGroundSettling(), "shallow lateral tread contact retains neutral recovery above a clear landing");
        Physics.Penetration = (a,p,ar,b,q,br) =>
        { Physics.PenetrationDirection = Vector3.forward; Physics.PenetrationDistance = .09f; return true; };
        Check(physics.NeedsGroundSettling(), "native contact skin is included in the shallow lateral landing allowance");
        var wall = new GameObject("wall along native resolution").Add(new Collider());
        Physics.Cast = (a,b,r,d,length) => a.z+d.z*length > .02f ? new[] {new RaycastHit {collider=wall}} : new RaycastHit[0];
        Check(!physics.NeedsGroundSettling(), "lateral depenetration toward an obstructed landing is rejected");
        Physics.Cast = (a,b,r,d,length) => new RaycastHit[0];
        Physics.Penetration = (a,p,ar,b,q,br) =>
        { Physics.PenetrationDirection = Vector3.forward; Physics.PenetrationDistance = .2f; return true; };
        Check(!physics.NeedsGroundSettling(), "deep sideways ground penetration is not treated as a harmless tread lip");
    }
    private static void SupportedAirborneWalking()
    {
        var w = new World(); var floor = w.AddFloor((x,z) => Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        w.Chef.transform.position = new Vector3(0,.5f,-1);
        var physics = w.Ready(); w.Chef.GroundCollider = null; Vector3 input;
        Check(physics.TryLandingWalk(Vector3.zero,.05f,out input) && input.z > .9f,
            "airborne input continues above a certified connected landing corridor");
        w.Chef.GroundCollider = floor;
        Check(physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "stale native contact can continue over the same independently checked landing corridor");
        var contact = w.Chef.GetComponent<GroundCast>();
        contact.Normal = new Vector3(0,.8f,-.6f);
        Check(physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "a stale walkable slope normal permits the checked native movement envelope");
        var nativeObstacle = new GameObject("obstacle reached only by native slope motion").Add(new Collider());
        Physics.Overlap = (a,b,r) => b.y+r > 1.6f ? new Collider[] {nativeObstacle} : new Collider[0];
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "the native slope's upward velocity expands overhead clearance even from zero body velocity");
        Physics.Overlap = (a,b,r) => new Collider[0];
        contact.Normal = new Vector3(.6f,.8f,0);
        w.Chef.Motion.Velocity = Vector3.up * 3;
        Physics.Cast = (a,b,r,d,length) => a.x+d.x*length > .08f ? new[] {new RaycastHit {collider=nativeObstacle}} : new RaycastHit[0];
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "retained native normal velocity cannot move sideways into an obstruction missed by flat input");
        Physics.Cast = (a,b,r,d,length) => new RaycastHit[0];
        w.Chef.Motion.Velocity = Vector3.zero;
        contact.Normal = new Vector3(0,.5f,-.8660254f);
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "an unsupported native ground normal forbids landing steering");
        contact.Normal = Vector3.up;
        w.Chef.GroundCollider = null;
        var ceiling = new GameObject("ceiling inside airborne envelope").Add(new Collider());
        Physics.Overlap = (a,b,r) => b.y+r > 1.25f ? new Collider[] {ceiling} : new Collider[0];
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input),
            "grounded clearance alone cannot authorize an airborne body under a low ceiling");
        Physics.Overlap = (a,b,r) => new Collider[0];
        w.Chef.Motion.Velocity = new Vector3(4,0,0);
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input), "airborne continuation rejects a sharp turn against native momentum");
        w.Chef.Motion.Velocity = Vector3.zero;
        floor.Elevation = (x,z) => z < -.6f || z > .5f ? 0 : (float?)null;
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input), "airborne steering cannot bridge missing support to the next route point");
        floor.enabled = false;
        Check(!physics.TryLandingWalk(Vector3.zero,.05f,out input), "loss of current landing support forbids airborne continuation");
    }
    private static Teleportal AddPortal(World w, Vector3 position)
    {
        var go = new GameObject("portal"); go.transform.position = position;
        var portal = go.Add(new Teleportal()); go.Add(new ServerTeleportal());
        go.Add(new TeleportalPlayerSender()); go.Add(new ServerTeleportalPlayerSender());
        var point = new GameObject("attach"); point.transform.position = position + Vector3.up;
        point.transform.parent = go.transform; portal.m_teleportPoint = point.transform;
        go.Add(new TeleportalPlayerReceiver { m_attachPoint = point.transform,
            m_ReceiverAnimation = new ManualAnimation { m_TargetPositionValue = new Vector3(.9f,0,0) } });
        go.Add(new ServerTeleportalPlayerReceiver());
        w.Colliders.Add(go.Add(new BoxCollider { isTrigger = true, center = new Vector3(0,.5f,0), size = new Vector3(.6f,1,.6f) }));
        return portal;
    }
    private static World PortalWorld(out Teleportal a, out Teleportal b)
    {
        var w = new World();
        w.AddFloor((x,z) => Math.Abs(z)<2 && ((x>-1 && x<4) || (x>9 && x<14)) ? 0 : (float?)null);
        a = AddPortal(w, Vector3.zero); b = AddPortal(w, new Vector3(10,0,0));
        a.m_exitPortal = b.gameObject; // Deliberately one-way.
        w.Chef.gameObject.Add(new ServerTeleportablePlayer());
        w.Chef.gameObject.Add(new ClientTeleportablePlayer());
        w.Chef.transform.position = new Vector3(1.7f,0,0);
        Physics.Overlap = (low,high,r) => w.Colliders.Where(c => c is BoxCollider && c.enabled &&
            low.x >= c.bounds.min.x-r && low.x <= c.bounds.max.x+r &&
            low.z >= c.bounds.min.z-r && low.z <= c.bounds.max.z+r &&
            high.y+r>c.bounds.min.y && low.y-r<c.bounds.max.y).ToArray();
        Physics.Cast = (low,high,r,d,length) => Enumerable.Range(0,Math.Max(2,(int)(length/.02f)+2))
            .SelectMany(i => Physics.Overlap(low+d*(length*i/Math.Max(1,(int)(length/.02f)+1)),high+d*(length*i/Math.Max(1,(int)(length/.02f)+1)),r))
            .Distinct().Select(c => new RaycastHit {collider=c}).ToArray();
        return w;
    }
    private static void Portals()
    {
        Teleportal a,b; var w = PortalWorld(out a,out b); var physics = w.Ready();
        PortalLink link = physics.Portal(a.GetInstanceID());
        Check(link != null && link.Valid && Math.Abs(link.Exit.x-10.9f)<.001f,"portal landing follows receiver animation and grounded attach point");
        var flushWall = new GameObject("wall behind native portal lip");
        var flushShape = flushWall.Add(new BoxCollider { center = new Vector3(.05f,.5f,0), size = new Vector3(.3f,1,1) });
        w.Colliders.Add(flushShape); physics.Refresh(w.Chef);
        Check(link.Valid,"portal contact remains outside a flush wall with the planning body margin");
        flushShape.enabled=false; physics.Refresh(w.Chef);
        Check(!physics.Segment(w.Chef.transform.position,link.Entry),"ordinary walking still blocks portal trigger");
        Check(physics.PortalEntry(w.Chef.transform.position,link),"selected portal permits its supported entry connector");
        Check(!physics.PortalEntry(new Vector3(-.8f,0,0),link),"entry from outside native activation cone is rejected");
        var search = new WalkSearch(physics.Starts(),physics.Neighbours,16000,()=>true);
        WalkCell far = search.Settled.First(c=>c.Portal==0 && c.X==30 && c.Z==0);
        Check(search.Complete && search.Route(far).Count(c=>c.Portal!=0)==1,"Dijkstra crosses a disconnected island with an explicit portal edge");
        Check(search.Distances[far]<4,"transport displacement is not charged as walking distance");
        var portalStart=new WalkCell(5,0,0);
        Check(!physics.Neighbours(new WalkCell(5,0,2)).Any(e=>e.Cell.Portal!=0),
            "graph does not postpone a lateral turn until the narrow portal frame");
        Check(physics.Neighbours(portalStart).Any(e=>e.Cell.Portal==a.GetInstanceID()),"portal connector is cached after a valid first search");
        int queries=physics.Queries;
        Check(physics.Neighbours(portalStart).Any(e=>e.Cell.Portal==a.GetInstanceID()) && physics.Queries==queries,
            "warm portal expansion reuses geometry instead of exhausting the planning budget");
        var obstacle=new GameObject("entry obstruction"); obstacle.transform.position=new Vector3(.85f,0,0);
        var obstruction=obstacle.Add(new BoxCollider {center=new Vector3(0,.5f,0),size=new Vector3(.1f,1,.5f)}); w.Colliders.Add(obstruction);
        physics.Refresh(w.Chef);
        Check(physics.Portal(a.GetInstanceID()).Valid && !physics.Neighbours(portalStart).Any(e=>e.Cell.Portal!=0),
            "new obstruction invalidates a cached entry without blocking the portal endpoint");
        obstruction.enabled=false; physics.Refresh(w.Chef);
        Check(physics.Neighbours(portalStart).Any(e=>e.Cell.Portal!=0),"removed obstruction restores cached entry connectivity");
        var conveyor=w.Chef.GroundCollider.gameObject.Add(new Belt {Velocity=new Vector3(5,0,0)});
        Time.realtimeSinceStartup+=.21f; physics.Refresh(w.Chef);
        Check(!physics.Neighbours(portalStart).Any(e=>e.Cell.Portal!=0),"cached portal connector still rejects live opposing flow");
        conveyor.Velocity=Vector3.zero; physics.Refresh(w.Chef);
        Check(physics.Neighbours(portalStart).Any(e=>e.Cell.Portal!=0),"changed flow restores cached portal connector without a geometry edit");
        var virtualCell = search.Route(far).First(c=>c.Portal!=0);
        Check(!virtualCell.Equals(new WalkCell(virtualCell.X,virtualCell.Y,virtualCell.Z)),"portal arrival and ordinary lattice nodes have distinct identity");
        w.Chef.transform.position=link.Exit; physics.LeavePortal(link); physics.Refresh(w.Chef);
        Check(!physics.Segment(link.Exit,new Vector3(9.1f,0,0)),"exit exemption cannot cross back through the portal to an equal or greater radius");
        Check(physics.Segment(link.Exit,new Vector3(11.5f,0,0)),"exit exemption permits walking outward");
        int departureHistory=0;
        Check(physics.PortalDeparture(link.Exit,new Vector3(10.8f,0,.4f),link,ref departureHistory),
            "an exit can join a nearer lattice cell when the ordinary body sweep stays clear of its trigger");
        Check(!physics.PortalDeparture(link.Exit,new Vector3(9.1f,0,0),link,ref departureHistory),
            "a non-outward portal departure never exempts crossing the trigger");
        w.Chef.transform.position = new Vector3(12,0,0); physics.Refresh(w.Chef);
        search = new WalkSearch(physics.Starts(),physics.Neighbours,16000,()=>true);
        Check(!search.Settled.Any(c=>c.X<10),"one-way portal does not invent a reverse edge");
        w.Chef.transform.position = new Vector3(1.7f,0,0);
        b.GetComponent<ServerTeleportalPlayerReceiver>().enabled = false; physics.Refresh(w.Chef);
        Check(!physics.Portal(a.GetInstanceID()).Valid,"disabled receiver invalidates link");
        b.GetComponent<ServerTeleportalPlayerReceiver>().enabled = true;
        a.gameObject.tag="Hazard"; physics.Refresh(w.Chef);
        Check(!physics.Portal(a.GetInstanceID()).Valid,"selected trigger exemption never masks a separate hazard");
        a.gameObject.tag="Untagged"; physics.Refresh(w.Chef);
        var wall = new GameObject("exit wall"); wall.transform.position = new Vector3(10.9f,0,0);
        var block = wall.Add(new BoxCollider {center=new Vector3(0,.5f,0),size=new Vector3(.5f,1,.5f)}); w.Colliders.Add(block);
        physics.Refresh(w.Chef); Check(!physics.Portal(a.GetInstanceID()).Valid,"blocked native landing removes portal from graph");
        block.enabled=false; physics.Refresh(w.Chef); Check(physics.Portal(a.GetInstanceID()).Valid,"clearing exit restores portal without restarting physics");
        w.AddFloor((x,z) => x>19 && x<24 && Math.Abs(z)<2 ? 2 : (float?)null);
        var third = AddPortal(w,new Vector3(20,2,0)); b.m_exitPortal=third.gameObject; third.m_exitPortal=a.gameObject;
        physics.Refresh(w.Chef); search=new WalkSearch(physics.Starts(),physics.Neighbours,16000,()=>true);
        var upper=search.Settled.First(c=>c.Portal==0 && c.X==55 && c.Y==10 && c.Z==0);
        Check(search.Complete && search.Route(upper).Count(c=>c.Portal!=0)==2,"multi-hop routes cross different heights and terminate despite a portal cycle");
        b.m_exitPortal=null; third.m_exitPortal=null;
        a.m_exitPortal=null; physics.Refresh(w.Chef); Check(physics.Portal(a.GetInstanceID())==null,"unlinking portal removes cached transport edge");

        w = PortalWorld(out a,out b);
        w.Colliders.OfType<Floor>().First().Elevation=(x,z)=>Math.Abs(z)<2 && ((x>-.5f && x<1.1f) || (x>9 && x<14)) ? 0 : (float?)null;
        w.Chef.transform.position=new Vector3(.85f,0,0); physics=w.Ready(); link=physics.Portal(a.GetInstanceID());
        search=new WalkSearch(physics.Starts(),physics.Neighbours,16000,()=>true);
        Check(link.StagingDistance<1 && search.Settled.Any(c=>c.Portal==0 && c.X==30),
            "shallow native portal platforms use a shorter supported staging approach");

        w = PortalWorld(out a,out b);
        var receiverOnly = new GameObject("moving receiver only");
        var attachOnly = new GameObject("moving attach"); attachOnly.transform.position = new Vector3(10,2.5f,0);
        var receiver = receiverOnly.Add(new TeleportalPlayerReceiver { m_attachPoint = attachOnly.transform,
            m_ReceiverAnimation = new ManualAnimation { m_TargetPositionValue = new Vector3(.9f,0,0) } });
        receiverOnly.Add(new ServerTeleportalPlayerReceiver());
        a.m_exitPortal = receiverOnly; physics = w.Ready(); link = physics.Portal(a.GetInstanceID());
        Check(link.Valid && link.ExitServer == null && link.ExitTrigger == null && Math.Abs(link.Exit.y)<.001f,
            "receiver-only endpoint accepts the native ray-miss fallback with a checked vertical landing");
        attachOnly.transform.position = new Vector3(10,3,0);
        physics.Refresh(w.Chef); Check(link.Valid,"three-unit festive receiver fall includes ground-probe contact margin");
        attachOnly.transform.position = new Vector3(10,3.5f,0);
        physics.Refresh(w.Chef); Check(link.Valid,"Carnival receiver supports its native 3.5-unit landing");
        attachOnly.transform.position += Vector3.up;
        physics.Refresh(w.Chef); Check(!link.Valid,"receiver fallback rejects a fall beyond the bounded landing sweep");
        attachOnly.transform.position = new Vector3(10,1,0); receiver.m_groundPlayer=false;
        physics.Refresh(w.Chef); Check(link.Valid,"native ungrounded receiver supports a safe vertical landing");
        Check(!physics.PortalLandingClear(new Vector3(10.9f,1,0),new Vector3(11.9f,0,0),null),
            "portal landing cannot excuse lateral displacement");
        var airborneWall = new GameObject("airborne exit obstruction"); airborneWall.transform.position=new Vector3(10.9f,1,0);
        w.Colliders.Add(airborneWall.Add(new BoxCollider { center=new Vector3(0,.2f,0),size=new Vector3(.3f,.3f,.3f) }));
        physics.Refresh(w.Chef); Check(!link.Valid,"clear floor below an obstructed native animation endpoint is rejected");
        w.Colliders.Last().enabled=false;
        var ceiling = new GameObject("native receiver ceiling");
        var ceilingShape=ceiling.Add(new BoxCollider { center=new Vector3(10.9f,2.8f,0),size=new Vector3(5,.4f,5) }); w.Colliders.Add(ceilingShape);
        attachOnly.transform.position = new Vector3(10,2,0);
        physics.Refresh(w.Chef); Check(link.Valid,"elevated native exit can separate downward from a level ceiling into checked clear space");
        ceiling.tag="Hazard"; Time.realtimeSinceStartup+=.21f; physics.Refresh(w.Chef);
        Check(!link.Valid,"ceiling separation never exempts hazards");

        w = PortalWorld(out a,out b);
        var station = w.AddStation(); station.transform.position = new Vector3(12,0,0);
        // The fixture's station selection is supplied independently of pathfinding.
        var navigation = w.Navigate(station);
        a.GetComponent<ServerTeleportal>().Ready = false;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State=="navigating" && (string)((Json.Record)navigation.Describe())["phase"]=="waiting_portal" && w.LastInput.sqrMagnitude==0,
            "cooldown keeps the action neutral before portal entry");
        a.GetComponent<ServerTeleportal>().Ready = true;
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(w.LastInput.sqrMagnitude==0,"portal entry first neutralizes the preceding walking command");
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check(w.LastInput.x<-.9f,"ready portal is entered using ordinary movement after braking");
        var clearPortalOverlap = Physics.Overlap;
        var movingBlocker = new GameObject("temporary receiver obstruction").Add(new BoxCollider());
        b.GetComponent<TeleportalPlayerReceiver>().m_attachPoint.position += new Vector3(.1f,0,0);
        Physics.Overlap = (low,high,r) => low.x > 9 ? new[] { movingBlocker } : clearPortalOverlap(low,high,r);
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State == "navigating" && w.LastInput.sqrMagnitude == 0 &&
            (string)((Json.Record)navigation.Describe())["phase"] == "waiting_portal_geometry",
            "a moving receiver's temporarily obstructed landing holds entry with neutral input");
        Physics.Overlap = clearPortalOverlap;
        b.GetComponent<TeleportalPlayerReceiver>().m_attachPoint.position -= new Vector3(.1f,0,0);
        Time.realtimeSinceStartup += .02f; navigation.Tick(w.Chef);
        Check(w.LastInput.x < -.9f, "entry resumes when the moving receiver landing is checked clear again");
        var traveller=w.Chef.GetComponent<ServerTeleportablePlayer>(); traveller.Travelling=true;
        b.GetComponent<ServerTeleportalPlayerReceiver>().Receiving=true;
        w.Chef.enabled=false; w.Chef.GetComponent<CapsuleCollider>().enabled=false;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check(navigation.NativeTeleportInProgress(w.Chef) && w.LastInput.sqrMagnitude==0 && navigation.Action.State=="navigating",
            "native transport keeps neutral ownership while chef collider and controls are disabled");
        // ManualAnimation fixes its destination when receiving begins, even if
        // the receiver keeps moving during the native animation.
        b.GetComponent<TeleportalPlayerReceiver>().m_attachPoint.position += new Vector3(1,0,0);
        traveller.Travelling=false; w.Chef.enabled=true; w.Chef.GetComponent<CapsuleCollider>().enabled=true;
        var landingFloor=w.Chef.GroundCollider;
        var ordinaryOverlap=Physics.Overlap;
        Physics.Overlap=(low,high,r)=>low.y-r<-.01f ? new[] { landingFloor } : ordinaryOverlap(low,high,r);
        Physics.Penetration=(ca,pa,ra,cb,pb,rb)=>cb==landingFloor;
        Physics.PenetrationDirection=Vector3.up; Physics.PenetrationDistance=.11f;
        w.Chef.transform.position=new Vector3(10.9f,-.105f,0); w.Chef.GroundCollider=null;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check(navigation.Action.State=="navigating" && w.LastInput.sqrMagnitude==0 &&
            (int)((Json.Record)navigation.Describe())["teleportsCompleted"]==0,
            "native exit floor penetration waits for checked PhysX recovery before counting the teleport");
        Physics.Overlap=ordinaryOverlap; Physics.Penetration=(ca,pa,ra,cb,pb,rb)=>false;
        w.Chef.GroundCollider=landingFloor; w.Chef.transform.position=new Vector3(10.9f,0,0);
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check((int)((Json.Record)navigation.Describe())["teleportsCompleted"]==1,"linked native arrival records exactly one completed transport");
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        ((System.Collections.IDictionary)typeof(NavigationActions).GetField("available",flags).GetValue(navigation)).Clear();
        typeof(NavigationActions).GetField("unavailable",flags).SetValue(navigation,"planning_in_progress");
        Check((bool)typeof(NavigationActions).GetMethod("ApplyPlan",flags).Invoke(navigation,null),
            "budget exhaustion after native arrival retains the checked tail instead of walking back across the portal");
        ((System.Collections.IDictionary)typeof(NavigationActions).GetField("available",flags).GetValue(navigation)).Clear();
        typeof(NavigationActions).GetField("unavailable",flags).SetValue(navigation,null);
        Check(!(bool)typeof(NavigationActions).GetMethod("ApplyPlan",flags).Invoke(navigation,null) &&
            navigation.Action.State == "navigating" && w.LastInput.sqrMagnitude == 0 &&
            (string)((Json.Record)navigation.Describe())["phase"] == "waiting_portal_route",
            "moving receiver arrival waits neutrally when the onward route is temporarily disconnected");
        Time.realtimeSinceStartup += 16;
        Check(!(bool)typeof(NavigationActions).GetMethod("ApplyPlan",flags).Invoke(navigation,null) &&
            navigation.Action.Reason == "destination_unreachable", "moving receiver replan wait is bounded");
        navigation.Stop("stopped"); Check(!navigation.NativeTeleportInProgress(w.Chef),"stop clears portal control exception");

        w=PortalWorld(out a,out b); station=w.AddStation(); station.transform.position=new Vector3(12,0,0);
        w.Chef.transform.position=new Vector3(1.7f,0,.5f);
        navigation=w.Navigate(station); Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check((string)((Json.Record)navigation.Describe())["phase"]=="entering_portal" && w.LastInput.sqrMagnitude==0,
            "portal braking lookahead reaches beyond the ordinary 1.4-unit walking shortcut");
        navigation.Stop("stopped");

        w=PortalWorld(out a,out b); station=w.AddStation(); station.transform.position=new Vector3(12,0,0);
        navigation=w.Navigate(station); a.GetComponent<ServerTeleportal>().Ready=false;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Time.realtimeSinceStartup+=8; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason=="teleport_timeout","permanently busy portal ends within a bounded timeout");

        w=PortalWorld(out a,out b); station=w.AddStation(); station.transform.position=new Vector3(12,0,0);
        navigation=w.Navigate(station); Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        w.Chef.GetComponent<ServerTeleportablePlayer>().Travelling=true;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        w.Chef.GetComponent<ServerTeleportablePlayer>().Travelling=false;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason=="teleport_exit_mismatch" && (int)((Json.Record)navigation.Describe())["teleportsCompleted"]==0,
            "native flag alone cannot establish arrival at the linked exit");

        w=PortalWorld(out a,out b); station=w.AddStation(); station.transform.position=new Vector3(12,0,0);
        navigation=w.Navigate(station); Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        a.GetComponent<ServerTeleportalPlayerSender>().enabled=false;
        Time.realtimeSinceStartup+=.02f; navigation.Tick(w.Chef);
        Check(navigation.Action.Reason=="teleport_unavailable","disabling sender interrupts an active portal entry");
    }
    private static void NativeFlatMeshSeam()
    {
        var w=new World(); var floor=w.AddFloor((x,z)=>0);
        var cast=Physics.GroundSphere;
        Physics.GroundSphere=(p,r,d,length)=>
        {
            var hits=cast(p,r,d,length);
            if(p.x>.5f && p.x<.6f)
                for(int i=0;i<hits.Length;i++)
                {
                    hits[i].normal=new Vector3(.036618646f,.998241961f,-.046605986f);
                    hits[i].distance+=r*(1-hits[i].normal.y);
                }
            return hits;
        };
        var physics=w.Ready();
        Check(physics.Segment(Vector3.zero,new Vector3(1,0,0)),"native flat mesh sphere-contact seam is resolved by an independent plane ray");
        Physics.Ray=(p,d,length,mask)=>new RaycastHit[0];
        Check(!physics.Segment(Vector3.zero,new Vector3(1,0,0)),"missing centre support does not receive flat seam correction");
        Physics.Ray=(p,d,length,mask)=>new[]{new RaycastHit {collider=floor,point=new Vector3(p.x,.01f,p.z),normal=Vector3.up,distance=p.y-.01f}};
        Check(!physics.Segment(Vector3.zero,new Vector3(1,0,0)),"a ray at a different ledge height cannot explain a sphere-contact seam");
    }
    private static void NativeArrivalBraking()
    {
        var w=new World(); w.AddFloor((x,z)=>Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(0,0,-1.35f); var station=w.AddStation(); var navigation=w.Navigate(station);
        Time.deltaTime=Time.unscaledDeltaTime=.05f; w.Chef.Motion.Velocity=new Vector3(0,0,4);
        Time.realtimeSinceStartup+=.05f; navigation.Tick(w.Chef);
        Check(w.LastInput.sqrMagnitude==0 && navigation.Action.State=="navigating", "neutral starts before delayed native motion overshoots the final stance");
        var receipt=(Json.Record)((Json.Record)navigation.Describe())["arrival"];
        Check(!(bool)receipt["positioned"],"predicted arrival cannot substitute for actual position and settling");
        w.Chef.transform.position=new Vector3(0,0,-1.15f); w.Chef.Motion.Velocity=Vector3.zero;
        for(int i=0;i<5;i++) {Time.realtimeSinceStartup+=.05f; navigation.Tick(w.Chef);}
        Check(navigation.Action.Reason=="arrived","braking still finishes only after actual native settling");
    }
    private static void IdlePlanningCadence()
    {
        var w=new World(); w.AddFloor((x,z)=>Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null); w.AddStation();
        var navigation=new NavigationActions((direction,ms)=>{},()=>{});
        navigation.Available(w.Chef);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var reason=typeof(NavigationActions).GetField("unavailable",flags);
        reason.SetValue(navigation,"planning_in_progress");
        long revision=(long)((Json.Record)navigation.Describe())["catalogRevision"];
        long generation=(long)((Json.Record)((Json.Record)navigation.Describe())["diagnostics"])["searchGeneration"];
        for(int i=0;i<5;i++) { navigation.Tick(w.Chef); Time.realtimeSinceStartup+=.001f; }
        Check((long)((Json.Record)navigation.Describe())["catalogRevision"]==revision,"catalogue and background tick cannot duplicate a slice in one render frame");
        Time.frameCount++; Time.realtimeSinceStartup+=.016f; navigation.Tick(w.Chef);
        var diagnostics=(Json.Record)((Json.Record)navigation.Describe())["diagnostics"];
        Check((long)((Json.Record)navigation.Describe())["catalogRevision"]==revision+1 && (bool)diagnostics["searchReused"] && (long)diagnostics["searchGeneration"]==generation,"stable idle graph continues next frame without the old 100ms delay or a new search");
        navigation.Available(w.Chef);
        Check((long)((Json.Record)navigation.Describe())["catalogRevision"]==revision+2,"explicit catalogue request still refreshes its observation");
        w.Chef.transform.position+=new Vector3(.2f,0,0);navigation.Available(w.Chef);
        diagnostics=(Json.Record)((Json.Record)navigation.Describe())["diagnostics"];
        Check(!(bool)diagnostics["searchReused"] && (long)diagnostics["searchGeneration"]==generation+1,"moving the chef discards old starting costs");
        long currentRevision=(long)((Json.Record)navigation.Describe())["catalogRevision"];
        navigation.Action.Start(99,"observation_only_test",true);
        Time.frameCount++;Time.realtimeSinceStartup+=.2f;navigation.WarmCatalogue(w.Chef);
        Check(navigation.Action.State=="navigating" && (long)((Json.Record)navigation.Describe())["catalogRevision"]==currentRevision,
            "observation-only warming cannot tick, cancel, or replan an input-owning action");
    }
    private static void SearchContinuity()
    {
        var w=new World(); var floor=w.AddFloor((x,z)=>Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        var material=floor.gameObject.Add(new PlayerPhysicsSurface()); var physics=w.Ready();
        Action remember=()=>{physics.Refresh(w.Chef);physics.Starts().ToArray();Check(physics.RememberSearch(),"stationary supported graph can remember a search proof");};
        remember(); Time.realtimeSinceStartup+=.02f;physics.Refresh(w.Chef);
        Check(physics.CanContinueSearch(),"unchanged geometry and movement rules permit continuation");
        // A worker may request a later batch without touching earlier supports.
        Check(physics.RememberSearch(true),"worker batches retain earlier support proofs even without re-expansion");
        material.Properties.SpeedMultiplier=.5f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"surface speed edits invalidate settled reachability");remember();
        material.Properties.Slidiness=.2f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"surface mode edits invalidate settled reachability");material.Properties.Slidiness=0;remember();
        Physics.queriesHitBackfaces=true;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"global physics query changes invalidate progress");Physics.queriesHitBackfaces=false;remember();
        var wall=new GameObject("new wall").Add(new BoxCollider());w.Colliders.Add(wall);physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"new collision geometry invalidates progress");remember();
        wall.transform.position+=Vector3.right;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"moving geometry invalidates progress");remember();
        w.Colliders.Remove(wall);physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"removed geometry invalidates progress");remember();
        w.Chef.Movement.RunSpeed=2;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"chef speed changes invalidate progress");remember();
        w.Chef.Movement.StepHeightMax=.3f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"chef step changes invalidate progress");remember();
        var zone=new GameObject("wind");var wind=zone.Add(new WindVolume());w.Colliders.Add(zone.Add(new BoxCollider {size=Vector3.one*20}));remember();
        physics.Refresh(w.Chef);Check(physics.CanContinueSearch(),"unchanged wind does not force a restart");
        wind.Velocity=Vector3.right;physics.Refresh(w.Chef);Check(!physics.CanContinueSearch(),"wind velocity changes invalidate progress");remember();
        wind.enabled=false;physics.Refresh(w.Chef);Check(!physics.CanContinueSearch(),"wind enabled state changes invalidate progress");remember();
        floor.gameObject.Add(new Belt());Time.realtimeSinceStartup+=.25f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"a newly discovered moving surface invalidates the old static proof");physics.Starts().ToArray();
        Check(!physics.RememberSearch(),"unversioned moving-surface predicates retain fresh-search fallback");
    }
    private static void ShallowCeilingContacts()
    {
        // Recorded s_sushi_1_1 floor top 0.05, two-unit chef and ceiling bottom 2.
        var w=new World();w.AddFloor((x,z)=>.05f);
        var body=w.Chef.GetComponent<CapsuleCollider>();body.height=2;body.radius=.4f;body.center=Vector3.up;
        w.Chef.transform.position=new Vector3(14.1310482f,.05000013f,-4.6000347f);
        var ceiling=new GameObject("Ceiling");ceiling.transform.position=new Vector3(14.39f,2.5f,-10.99017f);
        var roof=ceiling.Add(new BoxCollider {size=new Vector3(50,1,50)});w.Colliders.Add(roof);
        Physics.Overlap=(low,high,r)=>high.y+r>roof.bounds.min.y && low.y-r<roof.bounds.max.y ? new Collider[] {roof} : new Collider[0];
        Physics.Cast=(low,high,r,d,length)=>Math.Max(high.y,high.y+d.y*length)+r>roof.bounds.min.y ? new[] {new RaycastHit {collider=roof}} : new RaycastHit[0];
        var floorBox=new GameObject("raised floor contact").Add(new BoxCollider {center=new Vector3(14,-.2f,-4),size=new Vector3(50,.5f,50)});
        floorBox.gameObject.layer=9;w.Colliders.Add(floorBox);
        Physics.Overlap=(low,high,r)=>new Collider[] {roof,floorBox}.Where(c=>high.y+r>c.bounds.min.y && low.y-r<c.bounds.max.y).ToArray();
        Physics.Cast=(low,high,r,d,length)=>new Collider[] {roof,floorBox}.Where(c=>Math.Max(high.y,high.y+d.y*length)+r>c.bounds.min.y && Math.Min(low.y,low.y+d.y*length)-r<c.bounds.max.y).Select(c=>new RaycastHit {collider=c}).ToArray();
        var physics=w.Ready();
        Check(physics.Starts().Any(),"native-height ceiling contact does not erase every starting cell on the recorded raised floor");
        Check(physics.Segment(new Vector3(14,.05f,-4.4f),new Vector3(14.4f,.05f,-4.4f)),"the same bounded head contact allowance applies to walking sweeps");
        w.Chef.transform.position=new Vector3(14.1310482f,0,-4.6000347f);physics.Refresh(w.Chef);
        Check(physics.Starts().Any(),"the native resting pose can start while a raised floor consumes a shallow contact offset");
        Check(!physics.NeedsGroundSettling(),"a stable shallow flat floor contact does not wait forever for native depenetration");
        w.Chef.transform.position=new Vector3(14.1310482f,-.1f,-4.6000347f);physics.Refresh(w.Chef);
        Check(!physics.Starts().Any(),"deeper ground penetration cannot use the shallow resting-contact allowance");
        w.Chef.transform.position=new Vector3(14.1310482f,.05000013f,-4.6000347f);physics.Refresh(w.Chef);
        ceiling.tag="Hazard";Time.realtimeSinceStartup+=.21f;physics.Refresh(w.Chef);
        Vector3 point;Collider ground;
        Check(!physics.Stand(new Vector3(14,.05f,-4.4f),out point,out ground),"overhead hazards retain the full original query envelope");
        ceiling.tag="Untagged";ceiling.transform.position.y=2.4f;Time.realtimeSinceStartup+=.21f;physics.Refresh(w.Chef);
        Check(!physics.Stand(new Vector3(14,.05f,-4.4f),out point,out ground),"a genuinely lower ceiling remains blocked");
        ceiling.transform.position.y=2.5f;w.Chef.GetComponent<CapsuleCollider>().contactOffset=0;physics.Refresh(w.Chef);
        Check(!physics.Stand(new Vector3(14,.05f,-4.4f),out point,out ground),"no extra allowance is invented when native contact offset is zero");
        body.contactOffset=.01f;body.height=.8f;body.center=new Vector3(0,.4f,0);ceiling.transform.position.y=1.36f;
        physics.Refresh(w.Chef);
        Check(!physics.Stand(new Vector3(14,.05f,-4.4f),out point,out ground),"spherical capsules use the actual query top rather than overstating their contact allowance");
    }
    private static void EquivalentVerticalStarts()
    {
        var w=new World();w.AddFloor((x,z)=>Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        w.Chef.transform.position=new Vector3(.13f,0,.27f);var physics=w.Ready();
        physics.Starts().ToArray();Check(physics.RememberSearch(),"off-lattice start retains exact native seed proof");
        foreach(float y in new[] {-2.4e-6f,-1.9e-6f,-2.7e-6f,1e-6f})
        {
            w.Chef.transform.position=new Vector3(.13f,y,.27f);physics.Refresh(w.Chef);
            Check(physics.CanContinueSearch(),"vertical standing jitter reuses only bit-identical native start nodes and costs");
            Check(physics.RememberSearch(true),"revalidated starts retain all previous support dependencies");
        }
        Check(physics.StartProofRevalidations>0,"vertical continuity actually reruns the native start kernel");
        w.Chef.transform.position+=new Vector3(.0001f,0,0);physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"horizontal movement keeps its existing strict invalidation");
        w.Chef.transform.position=new Vector3(.13f,0,.27f);physics.Refresh(w.Chef);physics.Starts().ToArray();physics.RememberSearch();
        w.Chef.transform.position+=Vector3.up*.02f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"different exact connector costs reject even vertical-only movement");
        w.Chef.transform.position=Vector3.zero;physics.Refresh(w.Chef);physics.Starts().ToArray();physics.RememberSearch();
        w.Chef.transform.position=Vector3.up*1e-6f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"an exact zero-cost seed becoming nonzero is not rounded into equivalence");
        w.Chef.transform.position=new Vector3(.13f,0,.27f);physics.Refresh(w.Chef);physics.Starts().ToArray();physics.RememberSearch();
        var block=new GameObject("fresh connector obstruction").Add(new Collider());
        Physics.Cast=(a,b,r,d,l)=>new[] {new RaycastHit {collider=block}};
        w.Chef.transform.position+=Vector3.up*1e-6f;physics.Refresh(w.Chef);
        Check(!physics.CanContinueSearch(),"fresh native start collision veto cannot reuse an old connector proof");
    }
    private static void ConveyorSearchContinuity()
    {
        var w=new World();var floor=w.AddFloor((x,z)=>Math.Abs(x)<3 && Math.Abs(z)<3 ? 0 : (float?)null);
        var belt=floor.gameObject.Add(new Travelator {Velocity=new Vector3(2,0,0)});
        var neighbour=new GameObject("adjacent belt").Add(new Travelator {Velocity=new Vector3(2,0,0)});
        belt.Neighbours[0]=neighbour;neighbour.Neighbours[1]=belt;
        var physics=w.Ready();
        Action remember=()=>{physics.Refresh(w.Chef);physics.Starts().ToArray();Check(physics.RememberSearch(),"native conveyor graph can retain search progress");};
        Action changed=()=>{physics.Refresh(w.Chef);Check(!physics.CanContinueSearch(),"changed native conveyor rules invalidate settled reachability");};
        remember();physics.Refresh(w.Chef);Check(physics.CanContinueSearch(),"unchanged native belts permit bounded search continuation");
        belt.m_speed=3;changed();remember();
        belt.m_directionXZ=1;changed();remember();
        belt.m_border=.2f;changed();remember();
        belt.enabled=false;changed();remember();
        neighbour.m_speed=1;changed();remember();
        neighbour.m_directionXZ=1;changed();remember();
        neighbour.transform.position+=Vector3.right;changed();remember();
        neighbour.gameObject.activeInHierarchy=false;changed();remember();
        belt.m_gridManager=new GridManager();changed();remember();
        belt.m_gridIndex=new GridIndex {X=1};changed();remember();
        belt.Neighbours[0]=null;changed();remember();
        belt.Neighbours[0]=neighbour;changed();remember();
        w.Chef.SurfaceMovable.SetPrevious(neighbour);changed();remember();
        neighbour.Destroyed=true;changed();
    }
    private static void CertifiedStart()
    {
        var w=new World();w.AddFloor((x,z)=>Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        var station=w.AddStation();var navigation=new NavigationActions((v,ms)=>{},()=>{});
        Json.Record catalog=null;
        for(int i=0;i<20;i++) {catalog=(Json.Record)navigation.Available(w.Chef);if((bool)catalog["complete"])break;}
        var destination=((List<object>)catalog["destinations"]).Cast<Json.Record>().First(d=>(int)d["objectInstanceId"]==station.GetInstanceID());
        long generation=(long)((Json.Record)((Json.Record)navigation.Describe())["diagnostics"])["searchGeneration"];
        Check(navigation.Start(w.Chef,1,(string)destination["id"])==null,"a certified destination starts normally");
        var diagnostics=(Json.Record)((Json.Record)navigation.Describe())["diagnostics"];
        Check((bool)diagnostics["searchReused"] && (long)diagnostics["searchGeneration"]==generation,
            "starting navigation revalidates and retains the certified idle tree");
        navigation.Stop("test");w.Chef.Movement.RunSpeed=2;
        navigation.Start(w.Chef,1,(string)destination["id"]);
        diagnostics=(Json.Record)((Json.Record)navigation.Describe())["diagnostics"];
        Check(!(bool)diagnostics["searchReused"] && (long)diagnostics["searchGeneration"]>generation,
            "starting after movement rules change rebuilds the old tree");
    }
    private static void WorkerStartingStateChanges()
    {
        string root=System.IO.Directory.GetCurrentDirectory();
        using(var host=new WorkerHost(System.IO.Path.Combine(root,".tools/dotnet/dotnet"),System.IO.Path.Combine(root,"artifacts/plugin/worker/Worker.dll")))
        {
            host.Start();var clock=System.Diagnostics.Stopwatch.StartNew();
            while(!host.Ready && clock.Elapsed.TotalSeconds<8) System.Threading.Thread.Sleep(5);
            Check(host.Ready,"integration worker starts in a separate process");WorkerHost.Current=host;
            var w=new World();w.AddFloor((x,z)=>Math.Abs(x)<2 && Math.Abs(z)<2 ? 0 : (float?)null);w.AddStation();
            var navigation=new NavigationActions((v,ms)=>{},()=>{});int populated=0;
            try
            {
                for(int i=0;i<12;i++)
                {
                    // Held-animation numerical movement must not cause endless
                    // asynchronous generations with an empty destination list.
                    w.Chef.Movement.RunSpeed=3+i*.0001f;Time.frameCount++;Time.realtimeSinceStartup+=.02f;
                    var catalog=(Json.Record)navigation.Available(w.Chef);
                    if(((List<object>)catalog["destinations"]).Count>0)populated++;
                }
                Check(populated>=10,"changing starting rules retain native immediate destination discovery with a worker enabled");
            }
            finally {navigation.Dispose();WorkerHost.Current=null;}
        }
    }
    private static void LoosePickupBodies()
    {
        var w=new World();var floor=w.AddFloor((x,z)=>Math.Abs(x)<4 && Math.Abs(z)<4 ? 0 : (float?)null);
        var item=new GameObject("loose food");item.Add(new PhysicalAttachment());var attachment=item.Add(new ServerPhysicalAttachment());
        var child=new GameObject("food collider");child.transform.parent=item.transform;
        var shape=child.Add(new BoxCollider {center=new Vector3(0,.3f,0),size=new Vector3(.4f,.6f,.4f)});w.Colliders.Add(shape);
        Physics.Overlap=(a,b,r)=>new Collider[] {shape};Physics.Cast=(a,b,r,d,length)=>new[] {new RaycastHit {collider=shape}};
        var physics=w.Ready();Vector3 point;Collider support;
        Check(physics.SharesCurrentSupport(new Vector3(2,0,0)),"a distant source on the same native floor can request active planning before discovery completes");
        Check(!physics.SharesCurrentSupport(new Vector3(6,0,0)) && !physics.SharesCurrentSupport(new Vector3(2,3,0)),"the local-floor hint does not cross missing support or different decks");
        Check(physics.Stand(Vector3.zero,out point,out support) && support==floor,"loose pickup collider does not block or replace ground support");
        Check(physics.Segment(Vector3.zero,new Vector3(1,0,0)),"execution sweeps also ignore passable loose pickup descendants");
        physics.Starts().ToArray();Check(physics.RememberSearch(),"loose-object fixture retains its discovered graph");
        item.transform.position+=Vector3.right*.2f;physics.Refresh(w.Chef);
        Check(physics.CanContinueSearch(),"rolling food does not invalidate the walking discovery frontier");
        attachment.Attached=true;physics.Refresh(w.Chef);
        Check(!physics.Stand(Vector3.zero,out point,out support),"attaching an item restores conservative collision checks");
        attachment.Attached=false;item.Add(new ServerFireHazard());Time.realtimeSinceStartup+=.21f;physics.Refresh(w.Chef);
        Check(!physics.Stand(Vector3.zero,out point,out support),"hazards remain obstacles even when associated with a loose pickup");
        w=new World();w.AddFloor((x,z)=>0);var obstacle=new GameObject("solid kitchen geometry").Add(new BoxCollider());w.Colliders.Add(obstacle);
        Physics.Overlap=(a,b,r)=>new Collider[] {obstacle};physics=w.Ready();
        Check(!physics.Stand(Vector3.zero,out point,out support),"ordinary geometry without a pickup attachment still blocks navigation");
        Check(physics.SharesCurrentSupport(Vector3.zero),"a shared floor is only a planning hint and cannot bypass the obstructed walking proof");
    }
    private static void StationaryThrowStances()
    {
        var w=new World();float shift=0;
        var floor=w.AddFloor((x,z)=>Math.Abs(x-shift)<2 && Math.Abs(z)<2 ? 0 : (float?)null);
        floor.testBounds=new Bounds(new Vector3(0,-.05f,0),new Vector3(4,.1f,4));
        var nav=new NavigationActions((direction,ms)=>{w.LastInput=direction;w.LastMilliseconds=ms;},()=>{w.LastInput=Vector3.zero;});nav.PrepareStances(w.Chef);Vector3 stance;
        Check(!nav.TryStance(new Vector3(1.9f,0,0),out stance),"a stationary throw needs footprint support at an open flat edge, beyond rounded walking contact");
        Check(nav.TryStance(new Vector3(1.3f,0,0),out stance),"a fully supported stationary throw remains available");
        var aim=new GameObject("human on another platform");aim.transform.position=new Vector3(0,0,8);
        Check(nav.StartPosition(w.Chef,1,aim,new Vector3(1,0,0),true)==null,"checked release approach starts");
        shift=-2;floor.transform.position=new Vector3(shift,0,0);floor.testBounds=new Bounds(new Vector3(shift,-.05f,0),new Vector3(4,.1f,4));w.Chef.transform.position+=Vector3.right*shift;
        Time.realtimeSinceStartup+=.05f;Time.frameCount++;nav.Tick(w.Chef);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
        var target=typeof(NavigationActions).GetField("target",flags).GetValue(nav);
        Check(nav.Action.State=="navigating" && target!=null && Math.Abs(((Vector3)target.GetType().GetField("Approach",flags).GetValue(target)).x+1)<.01f,
            "release stance follows its certified support rather than staying over the water in world space");
        floor.enabled=false;Time.realtimeSinceStartup+=.05f;Time.frameCount++;nav.Tick(w.Chef);
        Check(nav.Action.State=="idle" && w.LastInput.sqrMagnitude<.001f,"lost release support neutralizes motion before another walking input");
    }
    private static void Main()
    {
        SearchContinuity(); ShallowCeilingContacts(); EquivalentVerticalStarts(); ConveyorSearchContinuity(); CertifiedStart(); IdlePlanningCadence(); NativeArrivalBraking(); NativeFlatMeshSeam(); Portals(); CapturedStairCorner(); CapturedStairContact(); GroundSettling(); ShallowLandingContact(); SupportedAirborneWalking(); StepsAndRamps(); InteriorFlow(); DirectedConveyors(); MovingBoundary(); EmptyHandsBin(); ArrivalMotion(); ApproachFallback(); SlowFrames();
        WorkerStartingStateChanges(); LoosePickupBodies(); StationaryThrowStances();
        Console.WriteLine("PASS: " + checks + " navigator integration checks (analytical geometry; not PhysX)");
    }
}
