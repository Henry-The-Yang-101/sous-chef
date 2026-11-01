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
    private sealed class World
    {
        public readonly List<Collider> Colliders = new List<Collider>();
        public readonly PlayerControls Chef;
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
    private static void Main()
    {
        StepsAndRamps(); InteriorFlow(); MovingBoundary();
        Console.WriteLine("PASS: " + checks + " navigator integration checks (analytical geometry; not PhysX)");
    }
}
