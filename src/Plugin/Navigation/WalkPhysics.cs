using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    // All calls run on Unity's main thread, using the same colliders as gameplay.
    // A fine lattice is only a search index; standability and every edge use physics.
    internal sealed class WalkPhysics
    {
        public const float Spacing = .4f, HeightBand = .2f, Range = 60f;
        private const float Skin = .045f;
        private static readonly FieldInfo landscape = typeof(GroundCast).GetField("m_landscapeMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo windFilter = typeof(WindVolume).GetField("m_windFilter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo gridSelection = typeof(PlayerControls).GetField("m_bGridSelection", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo interactionMask = typeof(PlayerControls).GetField("m_interactMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Collider[] nearby = new Collider[4096], overlaps = new Collider[128];
        private readonly RaycastHit[] hits = new RaycastHit[128];
        private readonly Dictionary<WalkCell, Probe> probes = new Dictionary<WalkCell, Probe>();
        private readonly Dictionary<WalkCell, List<WalkEdge>> edges = new Dictionary<WalkCell, List<WalkEdge>>();
        private readonly Dictionary<int, GeometryStamp> stamps = new Dictionary<int, GeometryStamp>();
        private readonly List<Bounds> changed = new List<Bounds>();
        private readonly Dictionary<int, WindVolume> winds = new Dictionary<int, WindVolume>();
        private readonly List<Collider> people = new List<Collider>();
        private readonly Dictionary<int, bool> dangers = new Dictionary<int, bool>();
        private readonly Dictionary<int, IMovingSurface> surfaces = new Dictionary<int, IMovingSurface>();
        private readonly Dictionary<int, PlayerPhysicsSurface> properties = new Dictionary<int, PlayerPhysicsSurface>();
        private readonly Dictionary<int, IGridLocation> grids = new Dictionary<int, IGridLocation>();
        private PlayerControls chef;
        private CapsuleCollider capsule;
        private GameObject held;
        private int groundMask, collisionMask, interactMask;
        private bool useGridSelection;
        private float radius, height, pivotToFoot, lastReconcile;
        public bool Overflow { get; private set; }
        public int Queries { get; private set; }
        public bool Changed { get; private set; }
        public float Radius { get { return radius; } }
        public Vector3 Position { get { Vector3 p = chef.transform.position; p.y -= pivotToFoot; return p; } }
        public IEnumerable<Collider> Colliders { get { for (int i = 0; i < nearbyCount; i++) yield return nearby[i]; } }
        private int nearbyCount;
        private sealed class Probe { public bool Valid; public Vector3 Point; public Collider Ground; }
        private sealed class GeometryStamp { public Collider Collider; public Bounds Bounds; public Matrix4x4 Matrix; public int Layer, Shape; public bool Trigger, Danger; }

        public void Reset()
        {
            chef = null; probes.Clear(); edges.Clear(); stamps.Clear(); dangers.Clear(); surfaces.Clear(); properties.Clear(); grids.Clear();
        }
        public string Refresh(PlayerControls player)
        {
            Queries = 0; Overflow = false; Changed = false;
            if (!(player.GetActiveControlsImpl() is PlayerControlsImpl_Default)) return "unsupported_control_mode";
            CapsuleCollider shape = player.GetComponent<CapsuleCollider>();
            GroundCast ground = player.GetComponent<GroundCast>();
            if (shape == null || !shape.enabled || shape.direction != 1 || ground == null || landscape == null || windFilter == null || gridSelection == null || interactionMask == null)
                return "unsupported_chef_geometry";
            if (player.GroundCollider == null) return "not_grounded";
            if (Vector3.Dot(player.transform.up, Vector3.up) < .99f) return "unsupported_gravity";
            if (chef != player || capsule != shape) Reset();
            chef = player; capsule = shape;
            useGridSelection = (bool)gridSelection.GetValue(chef);
            interactMask = ((LayerMask)interactionMask.GetValue(chef)).value;
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>();
            GameObject nextHeld = carrier == null ? null : carrier.InspectCarriedItem();
            if (held != nextHeld) { probes.Clear(); edges.Clear(); held = nextHeld; Changed = true; }
            int nextGroundMask = ((LayerMask)landscape.GetValue(ground)).value;
            int nextCollisionMask = 0;
            for (int i = 0; i < 32; i++) if (!Physics.GetIgnoreLayerCollision(chef.gameObject.layer, i)) nextCollisionMask |= 1 << i;
            float nextRadius = capsule.radius * Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x), Mathf.Abs(capsule.transform.lossyScale.z)) + .025f;
            float nextHeight = Mathf.Max(2 * nextRadius, capsule.height * Mathf.Abs(capsule.transform.lossyScale.y));
            // Unity's lossyScale jitters by a few float ULPs as a unit-scale chef
            // turns. Retain the padded dimensions through that numerical noise;
            // otherwise every turn discards the complete graph. The tolerance is
            // far smaller than the extra 0.025-unit collision margin.
            if (groundMask != nextGroundMask || collisionMask != nextCollisionMask ||
                Mathf.Abs(radius - nextRadius) > .0001f || Mathf.Abs(height - nextHeight) > .0001f)
            { probes.Clear(); edges.Clear(); Changed = true; radius = nextRadius; height = nextHeight; }
            groundMask = nextGroundMask; collisionMask = nextCollisionMask;
            pivotToFoot = chef.transform.position.y - capsule.bounds.min.y;
            nearbyCount = Physics.OverlapSphereNonAlloc(chef.transform.position, Range, nearby, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (nearbyCount == nearby.Length) { Overflow = true; return "physics_query_overflow"; }
            changed.Clear(); winds.Clear(); people.Clear();
            bool reconcile = Time.realtimeSinceStartup - lastReconcile >= .2f;
            if (reconcile)
            {
                dangers.Clear(); surfaces.Clear(); properties.Clear(); grids.Clear();
                lastReconcile = Time.realtimeSinceStartup;
            }
            var present = new HashSet<int>();
            for (int i = 0; i < nearbyCount; i++)
            {
                Collider c = nearby[i];
                if (c == null || Own(c)) continue;
                int id = c.GetInstanceID(); present.Add(id);
                WindVolume wind = c.GetComponent<WindVolume>();
                if (wind != null) winds[id] = wind;
                if (c.GetComponentInParent<PlayerControls>() != null) people.Add(c);
                GeometryStamp old;
                Bounds bounds = c.bounds; Matrix4x4 matrix = c.transform.localToWorldMatrix;
                int shapeKey = ShapeKey(c); bool danger = Dangerous(c);
                if (!stamps.TryGetValue(id, out old) || old.Collider != c || !old.Bounds.Equals(bounds) ||
                    old.Matrix != matrix || old.Layer != c.gameObject.layer || old.Trigger != c.isTrigger || old.Shape != shapeKey || old.Danger != danger ||
                    (reconcile && (c is MeshCollider || c is TerrainCollider)))
                {
                    if (old != null) changed.Add(old.Bounds);
                    changed.Add(bounds);
                    stamps[id] = new GeometryStamp { Collider = c, Bounds = bounds, Matrix = matrix, Layer = c.gameObject.layer, Trigger = c.isTrigger, Shape = shapeKey, Danger = danger };
                }
            }
            var removed = new List<int>();
            foreach (var pair in stamps) if (!present.Contains(pair.Key)) { changed.Add(pair.Value.Bounds); removed.Add(pair.Key); }
            foreach (int id in removed) { stamps.Remove(id); dangers.Remove(id); surfaces.Remove(id); properties.Remove(id); }
            // Primitive shapes are compared directly, so a stationary kitchen does
            // not throw away its complete graph five times a second. Mesh/terrain
            // regions still reconcile for in-place geometry edits preserving bounds.
            if (changed.Count > 0)
            {
                var dirty = new List<WalkCell>();
                foreach (var pair in probes)
                {
                    Vector3 p = Approximate(pair.Key);
                    foreach (Bounds bounds in changed)
                    {
                        float padding = radius + Spacing * 1.5f; // includes a full diagonal edge
                        // Probes reach about one unit below their foot, and a full
                        // chef height above. Symmetric height expansion would make
                        // a distant kill plane underneath every kitchen invalidate
                        // the entire graph on every mesh reconciliation.
                        Vector3 min = bounds.min, max = bounds.max;
                        if (p.x >= min.x - padding && p.x <= max.x + padding &&
                            p.z >= min.z - padding && p.z <= max.z + padding &&
                            p.y >= min.y - height - 1 && p.y <= max.y + 1)
                        { dirty.Add(pair.Key); break; }
                    }
                }
                // Invalidate every edge that could cross the changed region, while
                // retaining the rest of the kitchen when a person or wall moves.
                foreach (WalkCell cell in dirty) { probes.Remove(cell); edges.Remove(cell); }
                if (dirty.Count > 0) Changed = true;
            }
            return null;
        }
        private static int ShapeKey(Collider c)
        {
            unchecked
            {
                var box = c as BoxCollider;
                if (box != null) return box.center.GetHashCode() * 397 ^ box.size.GetHashCode();
                var sphere = c as SphereCollider;
                if (sphere != null) return sphere.center.GetHashCode() * 397 ^ sphere.radius.GetHashCode();
                var cap = c as CapsuleCollider;
                if (cap != null) return ((cap.center.GetHashCode() * 397 ^ cap.radius.GetHashCode()) * 397 ^ cap.height.GetHashCode()) * 397 ^ cap.direction;
                return c.GetType().GetHashCode();
            }
        }
        private bool Own(Collider c)
        {
            return c.transform.IsChildOf(chef.transform) || (held != null && c.transform.IsChildOf(held.transform));
        }
        private bool Dangerous(Collider c)
        {
            bool value; int id = c.GetInstanceID();
            if (!dangers.TryGetValue(id, out value))
            {
                value = c.GetComponentInParent<RespawnCollider>() != null || c.GetComponentInParent<ServerFireHazard>() != null ||
                    c.GetComponentInParent<Teleportal>() != null || c.CompareTag("Hazard");
                dangers[id] = value;
            }
            return value;
        }
        private bool Blocks(Collider c)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || Own(c)) return false;
            if (Dangerous(c)) return true;
            if (c.isTrigger) return false;
            if (c.GetComponentInParent<PlayerControls>() != null) return true;
            return (collisionMask & (1 << c.gameObject.layer)) != 0;
        }
        private bool Ground(Vector3 point, out RaycastHit best)
        {
            best = new RaycastHit(); float step = Mathf.Min(.65f, chef.Movement.StepHeightMax);
            int count = Physics.RaycastNonAlloc(point + Vector3.up * (step + .12f), Vector3.down, hits,
                2 * step + .24f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            float distance = Single.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || Own(hit.collider) || Dangerous(hit.collider) || hit.normal.y < .64f ||
                    Mathf.Abs(hit.point.y - point.y) > step + .04f || hit.distance >= distance) continue;
                best = hit; distance = hit.distance;
            }
            return distance != Single.MaxValue;
        }
        private void Capsule(Vector3 foot, out Vector3 low, out Vector3 high)
        {
            low = foot + Vector3.up * (radius + Skin);
            high = foot + Vector3.up * (height - radius + Skin);
        }
        public bool Stand(Vector3 guess, out Vector3 point, out Collider support)
        {
            point = guess; support = null; RaycastHit hit;
            if (!Ground(guess, out hit)) return false;
            point.y = hit.point.y; support = hit.collider;
            // Whole-footprint support: do not bridge a hole with the chef centre alone.
            for (int i = 0; i < 4; i++)
            {
                Vector3 offset = i < 2 ? Vector3.right * (i == 0 ? radius : -radius) : Vector3.forward * (i == 2 ? radius : -radius);
                RaycastHit edge;
                if (!Ground(point + offset, out edge) || Mathf.Abs(edge.point.y - point.y) > .22f) return false;
            }
            Vector3 low, high; Capsule(point, out low, out high);
            int count = Physics.OverlapCapsuleNonAlloc(low, high, radius, overlaps, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == overlaps.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++) if (Blocks(overlaps[i])) return false;
            foreach (Collider person in people)
            {
                if (person == null || person.attachedRigidbody == null) continue;
                Vector3 shift = person.attachedRigidbody.velocity * .12f; shift.y = 0;
                Bounds future = person.bounds; future.center += shift;
                future.Expand(new Vector3(radius * 2, 0, radius * 2));
                if (future.Contains(point + Vector3.up * (height * .5f))) return false;
            }
            return true;
        }
        private Vector3 Approximate(WalkCell cell) { return new Vector3(cell.X * Spacing, cell.Y * HeightBand, cell.Z * Spacing); }
        private Probe Sample(WalkCell hint)
        {
            Probe result;
            if (probes.TryGetValue(hint, out result)) return result;
            Vector3 point; Collider support;
            bool valid = Stand(Approximate(hint), out point, out support);
            result = new Probe { Valid = valid, Point = point, Ground = support }; probes[hint] = result;
            return result;
        }
        public Vector3 Point(WalkCell cell) { return Sample(cell).Point; }
        public IEnumerable<WalkEdge> Starts()
        {
            var result = new List<WalkEdge>(); Vector3 start = Position;
            int x = Mathf.RoundToInt(start.x / Spacing), z = Mathf.RoundToInt(start.z / Spacing), y = Mathf.RoundToInt(start.y / HeightBand);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            {
                var key = new WalkCell(x + dx, y, z + dz); Probe probe = Sample(key);
                if (probe.Valid && Segment(start, probe.Point)) result.Add(new WalkEdge(key, Vector3.Distance(start, probe.Point)));
            }
            return result;
        }
        public IEnumerable<WalkEdge> Neighbours(WalkCell cell)
        {
            List<WalkEdge> result;
            if (edges.TryGetValue(cell, out result)) return FilterFlow(cell, result);
            result = new List<WalkEdge>(); Probe from = Sample(cell);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                var next = new WalkCell(cell.X + dx, Mathf.RoundToInt(from.Point.y / HeightBand), cell.Z + dz);
                if ((Approximate(next) - Position).sqrMagnitude > (Range - 2) * (Range - 2)) continue;
                Probe to = Sample(next);
                if (to.Valid && ShortEdge(from.Point, to.Point)) result.Add(new WalkEdge(next, Vector3.Distance(from.Point, to.Point)));
            }
            edges[cell] = result; return FilterFlow(cell, result);
        }
        private IEnumerable<WalkEdge> FilterFlow(WalkCell from, List<WalkEdge> candidates)
        {
            var result = new List<WalkEdge>(); Probe a = Sample(from);
            foreach (WalkEdge edge in candidates)
            {
                Probe b = Sample(edge.Cell);
                if (CanWalkDirection(a.Point, b.Point, a.Ground) && CanWalkDirection(a.Point, b.Point, b.Ground)) result.Add(edge);
            }
            return result;
        }
        private bool ShortEdge(Vector3 from, Vector3 to)
        {
            RaycastHit middle;
            Vector3 midpoint = (from + to) * .5f;
            if (!Ground(midpoint, out middle) || Mathf.Abs(middle.point.y - midpoint.y) > .15f) return false;
            Vector3 delta = to - from, low, high; Capsule(from, out low, out high);
            int count = Physics.CapsuleCastNonAlloc(low, high, radius, delta.normalized, hits, delta.magnitude, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++) if (Blocks(hits[i].collider)) return false;
            return true;
        }
        public bool Segment(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; float length = delta.magnitude;
            int steps = Math.Max(1, Mathf.CeilToInt(length / .2f));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 point; Collider ground;
                Vector3 guess = Vector3.Lerp(from, to, (float)i / steps);
                if (!Stand(guess, out point, out ground) || Mathf.Abs(point.y - guess.y) > .22f || !CanWalkDirection(from, to, ground)) return false;
            }
            if (length < .001f) return true;
            Vector3 low, high; Capsule(from, out low, out high);
            int count = Physics.CapsuleCastNonAlloc(low, high, radius, delta / length, hits, length, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++) if (Blocks(hits[i].collider)) return false;
            return true;
        }
        public Vector3 Flow(Vector3 at, Collider ground)
        {
            Vector3 drift = Vector3.zero;
            if (ground != null)
            {
                IMovingSurface surface;
                if (!surfaces.TryGetValue(ground.GetInstanceID(), out surface))
                {
                    foreach (var c in ground.GetComponents<MonoBehaviour>()) if (c is IMovingSurface) { surface = (IMovingSurface)c; break; }
                    surfaces[ground.GetInstanceID()] = surface;
                }
                if (surface != null) drift += surface.CalculateVelocityAtPoint(at, null);
            }
            foreach (var pair in winds)
            {
                WindVolume wind = pair.Value;
                if (wind == null || !wind.isActiveAndEnabled || (((LayerMask)windFilter.GetValue(wind)).value & (1 << chef.gameObject.layer)) == 0) continue;
                var box = wind.GetComponent<BoxCollider>();
                Vector3 local = box.transform.InverseTransformPoint(at + Vector3.up * (height * .5f)) - box.center;
                Vector3 half = box.size * .5f;
                if (Mathf.Abs(local.x) <= half.x + radius && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z + radius) drift += wind.GetVelocity();
            }
            drift.y = 0; return drift;
        }
        public float Speed(Collider ground)
        {
            PlayerPhysicsSurface surface = null;
            if (ground != null && !properties.TryGetValue(ground.GetInstanceID(), out surface))
            { surface = ground.GetComponent<PlayerPhysicsSurface>(); properties[ground.GetInstanceID()] = surface; }
            var p = surface == null ? null : surface.Properties;
            if (p != null && (p.UseOverrideGravityNormal || p.Slidiness > .01f)) return 0; // unsupported gravity/sliding control
            return Mathf.Min(chef.Movement.MaxSpeed, chef.Movement.RunSpeed * chef.MovementScale) * (p == null ? 1 : p.SpeedMultiplier);
        }
        private bool CanWalkDirection(Vector3 from, Vector3 to, Collider ground)
        {
            if ((to - from).sqrMagnitude < .0001f) return true;
            Vector3 drift = Flow((from + to) * .5f, ground); float x, z;
            return WalkingSteering.Direction(to.x - from.x, to.z - from.z, drift.x, drift.z, Speed(ground), out x, out z);
        }
        public bool Sight(Vector3 foot, Collider target)
        {
            if ((interactMask & (1 << target.gameObject.layer)) == 0) return false;
            IGridLocation grid = null;
            if (useGridSelection && !grids.TryGetValue(target.GetInstanceID(), out grid))
            {
                for (Transform t = target.transform; t != null && grid == null; t = t.parent)
                    foreach (MonoBehaviour component in t.GetComponents<MonoBehaviour>())
                        if (component is IGridLocation) { grid = (IGridLocation)component; break; }
                grids[target.GetInstanceID()] = grid;
            }
            if (grid != null)
            {
                GridManager manager = grid.AccessGridManager;
                if (manager == null || !manager.isActiveAndEnabled) return false;
                GridIndex at = manager.GetUnclampedGridLocationFromPos(foot + Vector3.up * pivotToFoot), destination = grid.GridIndex;
                // Native grid selection only considers the four cardinal neighbours.
                // A geometrically close corner of the next counter is not a usable
                // approach to this counter, even if its collider is within reach.
                if (at.Y != destination.Y || Math.Abs(at.X - destination.X) + Math.Abs(at.Z - destination.Z) != 1) return false;
                GameObject occupied = manager.GetGridOccupant(at);
                if (occupied != null && !occupied.CompareTag("Hazard") && !occupied.CompareTag("Travelator") && !occupied.CompareTag("MovingPlatform")) return false;
                GameObject destinationObject = manager.GetGridOccupant(destination);
                if (destinationObject == null || !target.transform.IsChildOf(destinationObject.transform)) return false;
            }
            Vector3 origin = foot + Vector3.up * Mathf.Min(1f, height * .6f);
            Vector3 end = target.ClosestPointOnBounds(origin), delta = end - origin;
            if (delta.magnitude > (grid == null ? .9f : 1.1f) || Mathf.Abs(end.y - origin.y) > .8f) return false;
            int count = Physics.RaycastNonAlloc(origin, delta.normalized, hits, delta.magnitude, ~0, QueryTriggerInteraction.Ignore); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++)
                if (hits[i].collider != target && !hits[i].collider.transform.IsChildOf(target.transform) && Blocks(hits[i].collider)) return false;
            return true;
        }
    }
}
