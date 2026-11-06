using System;
using System.Collections.Generic;
using System.Reflection;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    // All calls run on Unity's main thread, using the same colliders as gameplay.
    // A fine lattice is only a search index; standability and every edge use physics.
    internal sealed partial class WalkPhysics
    {
        public const float Spacing = .4f, HeightBand = .2f, Range = 60f;
        private const float Skin = .045f;
        private static readonly FieldInfo landscape = typeof(GroundCast).GetField("m_landscapeMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo windFilter = typeof(WindVolume).GetField("m_windFilter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo gridSelection = typeof(PlayerControls).GetField("m_bGridSelection", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo interactionMask = typeof(PlayerControls).GetField("m_interactMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo previousSurface = typeof(SurfaceMovable).GetField("m_prevSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Collider[] nearby = new Collider[4096], overlaps = new Collider[128];
        private readonly List<Collider> settlingContacts = new List<Collider>();
        private readonly RaycastHit[] hits = new RaycastHit[128];
        private readonly Dictionary<WalkCell, Probe> probes = new Dictionary<WalkCell, Probe>();
        private readonly Dictionary<WalkCell, List<CachedEdge>> edges = new Dictionary<WalkCell, List<CachedEdge>>();
        private readonly Dictionary<int, GeometryStamp> stamps = new Dictionary<int, GeometryStamp>();
        private readonly Dictionary<int, MeshStamp> meshes = new Dictionary<int, MeshStamp>();
        private readonly List<Bounds> changed = new List<Bounds>();
        private readonly Dictionary<int, WindVolume> winds = new Dictionary<int, WindVolume>();
        private readonly Dictionary<int, bool> dangers = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> nativeSteps = new Dictionary<int, bool>();
        private readonly Dictionary<int, IMovingSurface> surfaces = new Dictionary<int, IMovingSurface>();
        private readonly Dictionary<int, IMovingSurface> surfaceHistories = new Dictionary<int, IMovingSurface>();
        private readonly Dictionary<int, PlayerPhysicsSurface> properties = new Dictionary<int, PlayerPhysicsSurface>();
        private readonly Dictionary<int, float> speeds = new Dictionary<int, float>();
        private readonly Dictionary<int, IGridLocation> grids = new Dictionary<int, IGridLocation>();
        private readonly HashSet<int> ignoredChefColliders = new HashSet<int>();
        private readonly HashSet<int> ignoredLooseColliders = new HashSet<int>();
        private PlayerControls chef;
        private CapsuleCollider capsule;
        private GameObject held;
        private int groundMask, collisionMask, interactMask;
        private bool useGridSelection;
        private float radius, nativeRadius, height, pivotToFoot, lastReconcile, walkingSpeed, stepHeight, bodyContactOffset;
        public bool TraceEnabled;
        public object StartFailures { get; private set; }
        public object Settlement { get; private set; }
        private string failureReason;
        private Vector3 failurePoint;
        private Collider failureCollider;
        private object failureSupport;
        public object Failure { get { return Json.Object("reason", failureReason, "point", Values.Vector(failurePoint),
            "collider", failureCollider == null ? null : failureCollider.gameObject.name,
            "colliderId", failureCollider == null ? 0 : failureCollider.GetInstanceID(), "support", failureSupport); } }
        private bool Fail(string reason, Vector3 point, Collider collider = null)
        { failureReason = reason; failurePoint = point; failureCollider = collider; failureSupport = null; return false; }
        public bool Overflow { get; private set; }
        public int Queries { get; private set; }
        public bool Changed { get; private set; }
        public float Radius { get { return radius; } }
        public bool OnConveyor { get { return MovingSurface(chef.GroundCollider) is Travelator; } }
        public float StepHeight { get { return Mathf.Min(.65f, chef.Movement.StepHeightMax); } }
        public Vector3 Position { get { Vector3 p = chef.transform.position; p.y -= pivotToFoot; return p; } }
        public IEnumerable<Collider> Colliders { get { for (int i = 0; i < nearbyCount; i++) yield return nearby[i]; } }
        public bool SharesCurrentSupport(Vector3 target)
        {
            if (chef == null || chef.GroundCollider == null || Mathf.Abs(target.y - Position.y) > 1.5f) return false;
            Vector3 origin = new Vector3(target.x, Position.y + .15f, target.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, .3f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
            if (count == hits.Length) return false;
            Collider nearest = null; float distance = Single.MaxValue;
            for (int i = 0; i < count; i++)
                if (hits[i].collider != null && !Own(hits[i].collider) && hits[i].distance < distance)
                { nearest = hits[i].collider; distance = hits[i].distance; }
            return nearest == chef.GroundCollider && !Dangerous(nearest);
        }
        private int nearbyCount;
        private int geometryScan, meshRevision;
        private sealed class Probe { public bool Valid; public Vector3 Point; public Collider Ground; public StationConnector Connector; }
        private struct GroundSample { public Vector3 Point; public Collider Ground; }
        private sealed class CachedEdge { public WalkEdge Edge; public GroundSample[] Support; }
        private sealed class GeometryStamp { public Collider Collider; public Bounds Bounds, Obstruction; public Matrix4x4 Matrix; public int Layer, Shape; public bool Trigger, Danger, Step; }
        private sealed class MeshStamp { public Mesh Mesh; public Vector3[] Vertices; public int[] Triangles; public int Scan, Seen, Revision; }

        public void Reset()
        {
            ClearStallAvoidance();
            ForgetSearch();
            chef = null; ClearGeometryCache(); stamps.Clear(); meshes.Clear(); dangers.Clear(); nativeSteps.Clear(); surfaces.Clear(); surfaceHistories.Clear(); properties.Clear(); grids.Clear(); ignoredChefColliders.Clear(); ignoredLooseColliders.Clear(); portals.Clear(); portalEntries.Clear(); portalExits.Clear(); exitPortal = null;
        }
        public string Refresh(PlayerControls player)
        {
            Queries = 0; Overflow = false; Changed = false; geometryCacheEvicted = false; speeds.Clear();
            if (!(player.GetActiveControlsImpl() is PlayerControlsImpl_Default)) return "unsupported_control_mode";
            CapsuleCollider shape = player.GetComponent<CapsuleCollider>();
            GroundCast ground = player.GetComponent<GroundCast>();
            if (shape == null || !shape.enabled || shape.direction != 1 || ground == null || landscape == null || windFilter == null || gridSelection == null || interactionMask == null)
                return "unsupported_chef_geometry";
            if (Vector3.Dot(player.transform.up, Vector3.up) < .99f) return "unsupported_gravity";
            if (chef != player || capsule != shape) Reset();
            chef = player; capsule = shape;
            walkingSpeed = Mathf.Min(chef.Movement.MaxSpeed, chef.Movement.RunSpeed * chef.MovementScale);
            useGridSelection = (bool)gridSelection.GetValue(chef);
            interactMask = ((LayerMask)interactionMask.GetValue(chef)).value;
            var carrier = chef.GetComponent<ClientPlayerAttachmentCarrier>();
            GameObject nextHeld = carrier == null ? null : carrier.InspectCarriedItem();
            if (held != nextHeld) { ClearGeometryCache(); held = nextHeld; Changed = true; }
            int nextGroundMask = ((LayerMask)landscape.GetValue(ground)).value;
            int nextCollisionMask = 0;
            for (int i = 0; i < 32; i++) if (!Physics.GetIgnoreLayerCollision(chef.gameObject.layer, i)) nextCollisionMask |= 1 << i;
            nativeRadius = capsule.radius * Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x), Mathf.Abs(capsule.transform.lossyScale.z));
            float nextRadius = nativeRadius + .025f;
            float nextBodyContact = Mathf.Min(.01f, Mathf.Max(0, capsule.contactOffset));
            float nextHeight = Mathf.Max(2 * nextRadius, capsule.height * Mathf.Abs(capsule.transform.lossyScale.y));
            // Unity's lossyScale jitters by a few float ULPs as a unit-scale chef
            // turns. Retain the padded dimensions through that numerical noise;
            // otherwise every turn discards the complete graph. The tolerance is
            // far smaller than the extra 0.025-unit collision margin.
            if (groundMask != nextGroundMask || collisionMask != nextCollisionMask || bodyContactOffset != nextBodyContact ||
                Mathf.Abs(radius - nextRadius) > .0001f || Mathf.Abs(height - nextHeight) > .0001f || stepHeight != StepHeight)
            { ClearGeometryCache(); Changed = true; radius = nextRadius; height = nextHeight; stepHeight = StepHeight; bodyContactOffset = nextBodyContact; }
            groundMask = nextGroundMask; collisionMask = nextCollisionMask;
            pivotToFoot = chef.transform.position.y - capsule.bounds.min.y;
            nearbyCount = Physics.OverlapSphereNonAlloc(chef.transform.position, Range, nearby, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (nearbyCount == nearby.Length) { Overflow = true; return "physics_query_overflow"; }
            ignoredChefColliders.Clear();
            ignoredLooseColliders.Clear();
            var inputConfig = PlayerInputLookup.GetInputConfig();
            for (int i = 0; i < nearbyCount; i++)
            {
                Collider c = nearby[i];
                if (c == null) continue;
                var controls = c.GetComponentInParent<PlayerControls>();
                if (controls != null)
                {
                    var provider = controls.PlayerIDProvider;
                    var entry = provider == null || inputConfig == null ? null : inputConfig.GetInputConfigEntry(provider.GetID());
                    if (controls == chef || (entry != null && entry.IsLocal())) ignoredChefColliders.Add(c.GetInstanceID());
                }
                var physical = c.GetComponentInParent<PhysicalAttachment>();
                var attachment = physical == null ? null : physical.GetComponent<ServerPhysicalAttachment>();
                if (attachment != null && !attachment.IsAttached()) ignoredLooseColliders.Add(c.GetInstanceID());
            }
            changed.Clear(); winds.Clear();
            geometryScan++;
            bool reconcile = Time.realtimeSinceStartup - lastReconcile >= .2f;
            if (reconcile)
            {
                dangers.Clear(); nativeSteps.Clear(); surfaces.Clear(); properties.Clear(); grids.Clear();
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
                GeometryStamp old;
                Bounds bounds = c.bounds; Matrix4x4 matrix = c.transform.localToWorldMatrix;
                Bounds obstruction = bounds;
                int shapeKey = ShapeKey(c, reconcile); bool danger = Dangerous(c), step = NativeStep(c);
                if (!stamps.TryGetValue(id, out old) || old.Collider != c || !old.Bounds.Equals(bounds) ||
                    !old.Obstruction.Equals(obstruction) || old.Matrix != matrix || old.Layer != c.gameObject.layer || old.Trigger != c.isTrigger || old.Shape != shapeKey || old.Danger != danger || old.Step != step ||
                    (reconcile && c is TerrainCollider))
                {
                    if (old != null) changed.Add(old.Obstruction);
                    changed.Add(obstruction);
                    stamps[id] = new GeometryStamp { Collider = c, Bounds = bounds, Obstruction = obstruction, Matrix = matrix, Layer = c.gameObject.layer, Trigger = c.isTrigger, Shape = shapeKey, Danger = danger, Step = step };
                }
            }
            var removed = new List<int>();
            foreach (var pair in stamps) if (!present.Contains(pair.Key)) { changed.Add(pair.Value.Obstruction); removed.Add(pair.Key); }
            foreach (int id in removed) { stamps.Remove(id); dangers.Remove(id); nativeSteps.Remove(id); surfaces.Remove(id); properties.Remove(id); }
            if (reconcile)
            {
                removed.Clear();
                foreach (var pair in meshes) if (pair.Value.Seen != geometryScan) removed.Add(pair.Key);
                foreach (int id in removed) meshes.Remove(id);
            }
            // Compare readable mesh contents at reconciliation rather than treating
            // every unchanged mesh as a geometry edit. Large kill-plane meshes can
            // otherwise invalidate an entire kitchen before its graph finishes warming.
            if (changed.Count > 0)
            {
                bool touchesLattice = InvalidateGeometryCache();
                sceneRevision++;
                // Wind fields use transformed, padded volumes and have their own
                // dependencies beyond the footprint of a walking capsule.
                if (touchesLattice || winds.Count > 0 || searchWinds.Count > 0) geometryVersion++;
            }
            RefreshPortals();
            if (Changed) geometryVersion++;
            return player.GroundCollider == null ? "not_grounded" : null;
        }
        private int ShapeKey(Collider c, bool reconcile)
        {
            unchecked
            {
                var box = c as BoxCollider;
                if (box != null) return box.center.GetHashCode() * 397 ^ box.size.GetHashCode();
                var sphere = c as SphereCollider;
                if (sphere != null) return sphere.center.GetHashCode() * 397 ^ sphere.radius.GetHashCode();
                var cap = c as CapsuleCollider;
                if (cap != null) return ((cap.center.GetHashCode() * 397 ^ cap.radius.GetHashCode()) * 397 ^ cap.height.GetHashCode()) * 397 ^ cap.direction;
                var meshCollider = c as MeshCollider;
                if (meshCollider != null) return MeshKey(meshCollider.sharedMesh, reconcile) * 2 + (meshCollider.convex ? 1 : 0);
                return c.GetType().GetHashCode();
            }
        }
        private int MeshKey(Mesh mesh, bool reconcile)
        {
            if (mesh == null) return 0;
            MeshStamp old;
            if (!meshes.TryGetValue(mesh.GetInstanceID(), out old) || old.Mesh != mesh)
            { old = new MeshStamp { Mesh = mesh, Scan = -1 }; meshes[mesh.GetInstanceID()] = old; }
            old.Seen = geometryScan;
            if (old.Revision != 0 && (!reconcile || old.Scan == geometryScan)) return old.Revision;
            old.Scan = geometryScan;
            Vector3[] vertices = null; int[] triangles = null;
            // Keep oversized/unreadable geometry conservative: its region still
            // invalidates every reconciliation, never silently assumes no edits.
            if (mesh.isReadable && mesh.vertexCount <= 65536)
            {
                vertices = mesh.vertices; triangles = mesh.triangles;
                if (triangles.Length > 524288) { vertices = null; triangles = null; }
            }
            bool same = vertices != null && old.Vertices != null &&
                vertices.Length == old.Vertices.Length && triangles.Length == old.Triangles.Length;
            if (same)
                for (int i = 0; i < vertices.Length; i++)
                    if (!vertices[i].Equals(old.Vertices[i])) { same = false; break; }
            if (same)
                for (int i = 0; i < triangles.Length; i++)
                    if (triangles[i] != old.Triangles[i]) { same = false; break; }
            if (!same) old.Revision = ++meshRevision;
            old.Vertices = vertices; old.Triangles = triangles;
            return old.Revision;
        }
        private bool Own(Collider c)
        {
            // Local chefs can be pushed. Their bodies and carried descendants do
            // not remove destinations or obstruct navigation. NPCs lack this component
            // and continue to participate in native collision and hazard queries.
            // Loose pickup bodies are also passable. Exclude them from geometry
            // stamps as well as sweeps so rolling food cannot restart discovery.
            return ignoredChefColliders.Contains(c.GetInstanceID()) || (held != null && c.transform.IsChildOf(held.transform)) ||
                (ignoredLooseColliders.Contains(c.GetInstanceID()) && !Dangerous(c));
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
            if (c == permittedPortalTrigger && c.isTrigger && !c.CompareTag("Hazard") &&
                c.GetComponentInParent<RespawnCollider>() == null && c.GetComponentInParent<ServerFireHazard>() == null) return false;
            if (Dangerous(c)) return true;
            if (c.isTrigger) return false;
            return (collisionMask & (1 << c.gameObject.layer)) != 0;
        }
        private bool BlocksWalkingBody(Collider obstacle, float lowestFoot, float highestFoot, float queryBottom, float queryTop)
        {
            if (!Blocks(obstacle)) return false;
            // Keep the original native query envelope. Only shallow vertical
            // contact with horizontal, nonhazard boxes can consume one native
            // contact offset (capped at 1 cm) beyond the existing contact Skin.
            // Side walls, slopes, hazards and deeper penetrations still block.
            if (bodyContactOffset <= 0 || Dangerous(obstacle) || !(obstacle is BoxCollider) ||
                Mathf.Abs(Vector3.Dot(obstacle.transform.up, Vector3.up)) < .9999f) return true;
            Bounds bounds = obstacle.bounds;
            if (bounds.min.y >= queryTop - bodyContactOffset && bounds.min.y > highestFoot + height * .5f) return false;
            if ((groundMask & (1 << obstacle.gameObject.layer)) != 0 &&
                bounds.max.y <= queryBottom + bodyContactOffset && bounds.max.y < lowestFoot + height * .5f) return false;
            return true;
        }
        private bool NativeStep(Collider c)
        {
            bool value; int id = c.GetInstanceID();
            // Native OnCollisionEnter requests Steppable on this exact object.
            if (!nativeSteps.TryGetValue(id, out value)) nativeSteps[id] = value = c.GetComponent<Steppable>() != null;
            return value;
        }
        private bool SupportConnected(RaycastHit from, RaycastHit to)
        {
            if (!WalkHeight.Connected(from.point.y, to.point.y, StepHeight)) return false;
            return ContinuousSupport(from, to) || NativeStep(to.point.y > from.point.y ? to.collider : from.collider);
        }
        private static bool ContinuousSupport(RaycastHit from, RaycastHit to)
        {
            Vector3 delta = to.point - from.point; delta.y = 0;
            float rise = to.point.y - from.point.y;
            float a = -Vector3.Dot(from.normal, delta) / from.normal.y;
            float b = -Vector3.Dot(to.normal, delta) / to.normal.y;
            // Continuous slopes (including a ramp joining a flat floor) do not
            // need a step component. A discontinuity between flat treads does.
            // Native sphere casts at rounded slope/tread joins have millimetre
            // error between the reconstructed foot and returned tangent normal.
            // Keep flat/flat ledges and near-flat mesh seams on the strict bound;
            // every accepted slope connector still undergoes the full body sweep.
            float tolerance = from.normal.y < .99f || to.normal.y < .99f ? .0025f : .0001f;
            return rise >= Mathf.Min(0, Mathf.Min(a, b)) - tolerance && rise <= Mathf.Max(0, Mathf.Max(a, b)) + tolerance;
        }
        private bool Ground(Vector3 point, out RaycastHit best, float dropDistance = -1)
        {
            best = new RaycastHit(); float step = StepHeight;
            // Match GroundCast's spherical contact for the chef centre. Rays miss
            // the rounded contact at the lips of native stair wedges.
            float r = nativeRadius;
            float reach = step + .12f;
            float drop = dropDistance < 0 ? reach : dropDistance;
            Vector3 origin = point + Vector3.up * (reach + r);
            int count = Physics.SphereCastNonAlloc(origin, r, Vector3.down, hits, reach + drop, groundMask, QueryTriggerInteraction.Ignore);
            Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            float distance = Single.MaxValue, contactHeight = 0;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || Own(hit.collider) || Dangerous(hit.collider) || hit.normal.y < .52992f ||
                    hit.distance <= 0 || hit.distance >= distance) continue;
                // SphereCast returns the contact point, not the capsule's foot.
                // Index and sweep the supported body pose; retain the native normal.
                Vector3 foot = origin + Vector3.down * (hit.distance + r);
                if (foot.y - point.y > reach - .08f || point.y - foot.y > drop - .08f) continue;
                best = hit; best.point = foot; distance = hit.distance; contactHeight = hit.point.y;
            }
            // PhysX can report a slightly tilted sphere contact at an internal
            // triangle seam on a flat mesh, lowering the reconstructed foot by
            // radius*(1-normal.y). Confirm the actual plane under the centre;
            // a hole, different collider, slope or real ledge is not corrected.
            if (distance != Single.MaxValue && best.normal.y > .99f && best.normal.y < .99999f &&
                contactHeight - best.point.y > 0 && contactHeight - best.point.y <= .002f)
            {
                int rays = Physics.RaycastNonAlloc(best.point + Vector3.up * .1f, Vector3.down, hits, .2f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
                if (rays == hits.Length) { Overflow = true; return false; }
                float closest = Single.MaxValue; RaycastHit plane = new RaycastHit();
                for (int i = 0; i < rays; i++) if (hits[i].distance < closest) { closest = hits[i].distance; plane = hits[i]; }
                if (plane.collider == best.collider && plane.normal.y > .99999f && Mathf.Abs(plane.point.y - contactHeight) < .0001f)
                { Vector3 foot = best.point; foot.y = plane.point.y; best.point = foot; best.normal = plane.normal; }
            }
            return distance != Single.MaxValue;
        }
        private void Capsule(Vector3 foot, out Vector3 low, out Vector3 high, float clearance = -1)
        {
            // Inset both vertical ends from resting contact. Translating the whole
            // capsule upward for floor skin makes a native-height ceiling block
            // every cell, even while the real chef comfortably stands below it.
            float r = clearance < 0 ? radius : clearance;
            low = foot + Vector3.up * Mathf.Min(r + Skin, height * .5f);
            high = foot + Vector3.up * Mathf.Max(height * .5f, height - r - Skin);
        }
        public bool Stand(Vector3 guess, out Vector3 point, out Collider support)
        { return Stand(guess, out point, out support, radius); }
        public bool StationaryStand(Vector3 guess, out Vector3 point, out Collider support)
        {
            RaycastHit contact;
            bool current = Vector3.Distance(guess, Position) < .02f;
            float footprint = current ? nativeRadius + .01f : radius;
            if (!Stand(guess, out point, out support, current ? ContactRadius : radius, out contact)) return false;
            if (NativeStep(support)) return true;
            var box = support as BoxCollider;
            bool flatBox = box != null && Mathf.Abs(box.transform.up.y) > .9999f;
            if (flatBox)
            {
                // Most release poses are inside one native floor box. Prove the
                // footprint algebraically; reserve native rays for its boundary.
                Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
                float x = box.transform.TransformVector(Vector3.right).magnitude;
                float z = box.transform.TransformVector(Vector3.forward).magnitude;
                if (x > 0 && z > 0 && Mathf.Abs(local.x) + footprint / x <= box.size.x * .5f &&
                    Mathf.Abs(local.z) + footprint / z <= box.size.z * .5f) return true;
            }
            else if (contact.normal.y < .99f) return true;
            // Rounded capsule contact is useful for walking across stair seams,
            // but is not a place to stop and aim over an open horizontal edge.
            for (int n = 0; n < 8; n++)
            {
                float angle = n * Mathf.PI / 4;
                Vector3 sample = point + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * footprint;
                int count = Physics.RaycastNonAlloc(sample + Vector3.up * .08f, Vector3.down, hits, .16f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
                if (count == hits.Length) { Overflow = true; return false; }
                bool found = false;
                for (int i = 0; i < count; i++)
                    if (hits[i].collider != null && !Own(hits[i].collider) && !Dangerous(hits[i].collider) &&
                        hits[i].normal.y > .99f && Mathf.Abs(hits[i].point.y - point.y) < .04f) { found = true; break; }
                if (!found) return Fail("release_footprint_unsupported", sample, support);
            }
            return true;
        }
        private bool Stand(Vector3 guess, out Vector3 point, out Collider support, float clearance)
        {
            RaycastHit centre;
            return Stand(guess, out point, out support, clearance, out centre);
        }
        private bool Stand(Vector3 guess, out Vector3 point, out Collider support, float clearance, out RaycastHit hit, float dropDistance = -1)
        {
            point = guess; support = null;
            if (!Ground(guess, out hit, dropDistance)) return Fail("ground_probe", guess);
            point.y = hit.point.y; support = hit.collider;
            // A travelator's rounded side contact is not a controllable lane.
            // The native belt pushes the chef off that lip, so routes must turn
            // on its top rather than shortcut around an overhanging corner.
            if (MovingSurface(support) is Travelator && !ConveyorSupport(point, hit.normal, clearance))
                return Fail("conveyor_side_support", point, support);
            if (!FloorCentreSupported(point, support)) return Fail("floor_centre_unsupported", point, support);
            if (!SlopeInterior(point, support, clearance)) return Fail("slope_side_clearance", point, support);
            // GroundCast supports the rounded capsule across narrow tread seams.
            // Requiring a ray under every footprint edge rejects real stairs even
            // while the native sphere has stable contact. Segment checks repeat
            // this support test along the entire route; broad gaps still fail.
            Vector3 low, high; Capsule(point, out low, out high, clearance);
            int count = Physics.OverlapCapsuleNonAlloc(low, high, clearance, overlaps, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == overlaps.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++) if (BlocksWalkingBody(overlaps[i], point.y, point.y, low.y - clearance, high.y + clearance)) return Fail("body_overlap", point, overlaps[i]);
            return true;
        }
        private bool FloorCentreSupported(Vector3 point, Collider support)
        {
            var box = support as BoxCollider;
            if (box == null || NativeStep(support) || Mathf.Abs(box.transform.up.y) < .9999f) return true;
            Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
            if (Mathf.Abs(local.x) <= box.size.x * .5f && Mathf.Abs(local.z) <= box.size.z * .5f) return true;
            // A sphere grazing a horizontal floor's outside edge reports a
            // sloping normal. It must not turn an unsupported inside corner into
            // a walkable ramp. Adjacent coplanar floors and real stair wedges can
            // still support the centre; native Steppable contact is kept above.
            int count = Physics.RaycastNonAlloc(point + Vector3.up * (nativeRadius + .01f), Vector3.down,
                hits, 2 * nativeRadius + .02f, groundMask, QueryTriggerInteraction.Ignore); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            float closest = Single.MaxValue; RaycastHit plane = new RaycastHit();
            for (int i = 0; i < count; i++)
                if (hits[i].collider != null && !Own(hits[i].collider) && hits[i].distance < closest)
                { closest = hits[i].distance; plane = hits[i]; }
            if (plane.collider == null || Dangerous(plane.collider) || plane.normal.y < .52992f) return false;
            return plane.normal.y < .99f || Mathf.Abs(plane.point.y - box.bounds.max.y) < .0025f;
        }
        // A sphere can touch a ramp while its centre is outside the side face.
        // That diagonal approach is standable in isolation but native slope
        // motion can push the next frame into the tread's vertical side. Reserve
        // the chef footprint across the ramp, using its live oriented geometry.
        private bool SlopeInterior(Vector3 point, Collider support, float clearance)
        {
            // Interior clearance shapes the planned route. A live connector can
            // safely leave that margin while descending onto the adjacent floor;
            // its actual capsule/support checks decide whether the exit is clear.
            if (clearance < radius - .0001f) return true;
            var box = support as BoxCollider;
            if (box == null) return true;
            Vector3 a = box.transform.TransformVector(Vector3.right);
            Vector3 b = box.transform.TransformVector(Vector3.up);
            Vector3 c = box.transform.TransformVector(Vector3.forward);
            if (Mathf.Abs(b.normalized.y) > .99f) return true;
            bool alongX = Mathf.Abs(a.normalized.y) < .01f;
            Vector3 lateral = alongX ? a : Mathf.Abs(c.normalized.y) < .01f ? c : Vector3.zero;
            float width = alongX ? box.size.x * a.magnitude : box.size.z * c.magnitude;
            if (lateral.sqrMagnitude < .001f || width < nativeRadius * 3) return true;
            float offset = Mathf.Abs(Vector3.Dot(point - box.transform.TransformPoint(box.center), lateral.normalized));
            return offset + nativeRadius + .1f <= width * .5f;
        }
        private Vector3 Approximate(WalkCell cell) { return new Vector3(cell.X * Spacing, cell.Y * HeightBand, cell.Z * Spacing); }
        private Probe Sample(WalkCell hint)
        {
            hint = GeometryCell(hint);
            Probe result;
            if (probes.TryGetValue(hint, out result)) return result;
            Vector3 point; Collider support;
            bool valid = Stand(Approximate(hint), out point, out support);
            result = new Probe { Valid = valid, Point = point, Ground = support }; CacheProbe(hint, result);
            return result;
        }
        public Vector3 Point(WalkCell cell) { return cell.Portal == 0 ? Sample(cell).Point : portals[cell.Portal].Exit; }
        public IEnumerable<WalkEdge> Starts()
        {
            var result = new List<WalkEdge>(); Vector3 start = Position;
            var failures = TraceEnabled ? new List<object>() : null;
            int x = Mathf.RoundToInt(start.x / Spacing), z = Mathf.RoundToInt(start.z / Spacing), y = Mathf.RoundToInt(start.y / HeightBand);
            // A corner contact may have no padded node in the immediate 3x3 ring.
            // Longer connectors still undergo complete support and collision checks.
            for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++)
            {
                var key = new WalkCell(x + dx, y, z + dz); Probe probe = Sample(key);
                int history = 0;
                if (probe.Valid && StartSegment(start, probe.Point, value => history = value))
                {
                    WalkCell resolved = Resolved(key, probe); resolved.SurfaceHistory = history;
                    result.Add(new WalkEdge(resolved, Vector3.Distance(start, probe.Point)));
                }
                else if (failures != null) failures.Add(Json.Object("node", Values.Vector(probe.Point), "standable", probe.Valid, "failure", probe.Valid ? Failure : null));
            }
            StartFailures = failures; currentStarts = result;
            return result;
        }
        public IEnumerable<WalkEdge> Neighbours(WalkCell cell)
        {
            if (cell.Portal != 0) return PortalDepartures(portals[cell.Portal]);
            return WithPortals(cell, WalkingNeighbours(cell));
        }
        private IEnumerable<WalkEdge> WalkingNeighbours(WalkCell cell)
        {
            List<CachedEdge> result;
            WalkCell geometryCell = GeometryCell(cell);
            if (edges.TryGetValue(geometryCell, out result)) return FilterFlow(cell, result);
            result = new List<CachedEdge>(); Probe from = Sample(cell);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                var next = new WalkCell(cell.X + dx, Mathf.RoundToInt(from.Point.y / HeightBand), cell.Z + dz);
                Probe to = Sample(next);
                if (!to.Valid || !WalkHeight.Connected(from.Point.y, to.Point.y, StepHeight)) continue;
                var support = new List<GroundSample>();
                if (Segment(from.Point, to.Point, radius, false, support))
                    result.Add(new CachedEdge { Edge = new WalkEdge(Resolved(next, to), Vector3.Distance(from.Point, to.Point)), Support = support.ToArray() });
            }
            // Cache eviction during neighbour production may have removed the
            // source. Keep every edge's key indexed for later invalidation.
            CacheProbe(geometryCell, from);
            edges[geometryCell] = result; return FilterFlow(cell, result);
        }
        private IEnumerable<WalkEdge> FilterFlow(WalkCell from, List<CachedEdge> candidates)
        {
            Probe a = Sample(from);
            foreach (CachedEdge cached in candidates)
            {
                WalkEdge edge = cached.Edge;
                Probe b = Sample(edge.Cell);
                // Radius is relative to the live chef, never part of cached
                // geometry. Moving closer must reveal previously clipped edges.
                if (!b.Valid || (b.Point - Position).sqrMagnitude > (Range - 2) * (Range - 2)) continue;
                if (!AvoidsStalledHeadings(a.Point, b.Point)) continue;
                bool clear = true;
                IMovingSurface current = MovingSurface(a.Ground), previous = null;
                if (from.SurfaceHistory != 0) surfaceHistories.TryGetValue(from.SurfaceHistory, out previous);
                foreach (GroundSample sample in cached.Support)
                {
                    TrackSurface(sample.Ground, sample.Point, ref current, ref previous);
                    if (sample.Ground == null || !CanWalkDirection(a.Point, b.Point, sample.Ground, sample.Point, previous)) { clear = false; break; }
                }
                if (clear)
                {
                    WalkCell destination = edge.Cell; destination.SurfaceHistory = SurfaceHistory(previous);
                    yield return new WalkEdge(destination, edge.Distance);
                }
            }
        }
        private static WalkCell GeometryCell(WalkCell cell) { return new WalkCell(cell.X, cell.Y, cell.Z); }
        private int SurfaceHistory(IMovingSurface surface)
        {
            var component = surface as Component;
            if (component == null) return 0;
            int id = component.GetInstanceID(); surfaceHistories[id] = surface; return id;
        }
        private WalkCell Resolved(WalkCell hint, Probe probe)
        {
            var key = new WalkCell(hint.X, Mathf.RoundToInt(probe.Point.y / HeightBand), hint.Z);
            // The same supported surface reached from above or below is one node.
            // Distinct decks at the same x/z retain distinct height keys.
            CacheProbe(key, probe);
            return key;
        }
        public bool Segment(Vector3 from, Vector3 to)
        { int history = 0; return Segment(from, to, ref history); }
        public bool Segment(Vector3 from, Vector3 to, ref int surfaceHistory)
        {
            bool entering = Vector3.Distance(from, Position) < .02f;
            int result = surfaceHistory;
            Collider saved = permittedPortalTrigger;
            if (entering && CanExitPortal(from, to)) permittedPortalTrigger = exitPortal;
            bool clear;
            try { clear = Segment(from, to, entering ? ContactRadius : radius, true, null, value => result = value, surfaceHistory); }
            finally { permittedPortalTrigger = saved; }
            surfaceHistory = result; return clear;
        }
        private bool Segment(Vector3 from, Vector3 to, float clearance, bool checkFlow, List<GroundSample> samples = null, Action<int> history = null, int initialHistory = 0)
        {
            // Recovery constraints are live routing policy, never cached geometry.
            if (checkFlow && !AvoidsStalledHeadings(from, to)) return false;
            Vector3 previous = from;
            RaycastHit previousGround = new RaycastHit(); bool havePrevious = false;
            IMovingSurface currentSurface = null, priorSurface = null;
            if (checkFlow && initialHistory == 0 && Vector3.Distance(from, Position) < .02f)
            {
                currentSurface = MovingSurface(chef.GroundCollider);
                if (currentSurface != null && chef.SurfaceMovable != null && previousSurface != null)
                    priorSurface = previousSurface.GetValue(chef.SurfaceMovable) as IMovingSurface;
            }
            Func<Vector3, int, bool> visit = null;
            visit = delegate(Vector3 guess, int depth)
            {
                Vector3 point; Collider ground; RaycastHit centre;
                // Only the connector from the actual chef uses contact clearance.
                // Growing a capsule while sliding away from a wall invents a
                // collision: use consistent native clearance along this connector.
                // Lattice nodes and edges retain padded clearance. The contact skin
                // is bounded, and every sample still checks support and hazards.
                if (!Stand(guess, out point, out ground, clearance, out centre)) return false;
                if (!WalkHeight.Connected(guess.y, point.y, StepHeight) || !WalkHeight.Connected(previous.y, point.y, StepHeight))
                    return Fail("support_height", point, ground);
                bool nativeStep = havePrevious && !ContinuousSupport(previousGround, centre) &&
                    NativeStep(point.y > previous.y ? centre.collider : previousGround.collider);
                // A coarse chord can bridge the unstandable strip at a wedge's
                // side: its sphere contact crosses the native slope cutoff there.
                // Refine changing support within the body skin, before certifying
                // the connection. Flat interiors retain the coarse sampling rate.
                Vector3 horizontal = point - previous; horizontal.y = 0;
                if (havePrevious && !nativeStep && horizontal.magnitude > Skin * .5f &&
                    (previousGround.collider != centre.collider || nativeRadius * (previousGround.normal - centre.normal).magnitude > Skin * .5f))
                {
                    if (depth >= 8) return Fail("support_resolution", point, ground);
                    if (!visit((previous + point) * .5f, depth + 1)) return false;
                    return visit(guess, depth + 1);
                }
                if (havePrevious && !SupportConnected(previousGround, centre))
                {
                    Fail("support_discontinuity", point, ground);
                    if (TraceEnabled) failureSupport = Json.Object("from", Values.Vector(previousGround.point),
                        "fromNormal", Values.Vector(previousGround.normal), "toNormal", Values.Vector(centre.normal));
                    return false;
                }
                if (checkFlow)
                {
                    if (!havePrevious && initialHistory != 0)
                    { currentSurface = MovingSurface(ground); surfaceHistories.TryGetValue(initialHistory, out priorSurface); }
                    TrackSurface(ground, centre.point, ref currentSurface, ref priorSurface);
                }
                if (checkFlow && !CanWalkDirection(from, to, ground, centre.point, priorSurface)) return false;
                // Native Steppable collisions can lift the body vertically. Ordinary
                // ramps cannot: sweep the actual continuous body trajectory, so
                // a raised endpoint cannot hide a collision at the ramp's side.
                if (!(nativeStep ? StepSweep(previous, point, clearance) : Sweep(previous, point, clearance))) return false;
                if (samples != null) samples.Add(new GroundSample { Point = centre.point, Ground = ground });
                previous = point; previousGround = centre; havePrevious = true;
                return true;
            };
            bool clear = WalkSegments.Check(Vector3.Distance(from, to), fraction => visit(Vector3.Lerp(from, to, fraction), 0));
            if (clear && !Overflow && history != null) history(SurfaceHistory(priorSurface));
            return clear && !Overflow;
        }
        // GroundCast intentionally loses contact when native stair momentum
        // carries the capsule more than 0.3 units above a tread. A clear landing
        // below the current body permits recovery while gravity settles it.
        // LandingWalking independently certifies any continuing movement; this
        // predicate alone never authorizes walking over a missing floor.
        public bool NeedsGroundSettling()
        {
            Settlement = null;
            if (chef == null) return false;
            Vector3 position = Position, support; Collider ground; RaycastHit hit;
            // GroundCast casts from pivot + 1 down for 2 units. Its search
            // range below a capsule foot is 1 + radius; this differs from the
            // maximum step UP. Route probes retain their normal step bounds.
            float landingDrop = 1f + nativeRadius;
            if (!Stand(position, out support, out ground, ContactRadius, out hit, landingDrop))
            { if (TraceEnabled) Settlement = Json.Object("rejected", "support", "failure", Failure); return false; }
            float gap = position.y - support.y;
            // Use the current body and live geometry. GroundCast's cached point
            // may still describe the preceding physics pose in this Update.
            // The support and sweep capsule already use Skin. At low Update
            // rates native motion can reach that skin before GroundCast's next
            // update; do not reject the same clear pose using a smaller epsilon.
            if (gap >= -Skin && gap <= landingDrop && (chef.GroundCollider == null || gap > .3f))
            {
                if (!Sweep(position, support, ContactRadius))
                { if (TraceEnabled) Settlement = Json.Object("rejected", "landing_sweep", "failure", Failure); return false; }
                if (TraceEnabled) Settlement = Json.Object("reason", "landing", "gap", gap, "support", ground.GetInstanceID());
                return true;
            }
            // A moving native capsule can temporarily penetrate a stair wedge.
            // A cast starting in that contact rejects every direction, including
            // escape. Prove that every inset-body overlap is walkable ground and
            // that PhysX resolves each real capsule contact upward. Treads and
            // wedges can overlap simultaneously. Let native physics perform that
            // resolution under neutral input; never move/teleport the chef.
            Vector3 low, high; Capsule(position, out low, out high, ContactRadius);
            int count = Physics.OverlapCapsuleNonAlloc(low, high, ContactRadius, overlaps, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == overlaps.Length) { Overflow = true; return false; }
            bool contact = false;
            var contacts = TraceEnabled ? new List<object>() : null;
            // Stand/Sweep reuse the query buffers; retain the original overlap
            // set while independently checking a native lateral depenetration.
            settlingContacts.Clear();
            for (int i = 0; i < count; i++) if (BlocksWalkingBody(overlaps[i], position.y, position.y, low.y - ContactRadius, high.y + ContactRadius)) settlingContacts.Add(overlaps[i]);
            foreach (Collider collider in settlingContacts)
            {
                if (Dangerous(collider) || (groundMask & (1 << collider.gameObject.layer)) == 0) return false;
                Vector3 direction; float distance;
                Queries++;
                if (!Physics.ComputePenetration(capsule, capsule.transform.position, capsule.transform.rotation,
                    collider, collider.transform.position, collider.transform.rotation, out direction, out distance) ||
                    distance <= 0 || distance > StepHeight)
                { if (TraceEnabled) Settlement = Json.Object("rejected", "penetration", "collider", collider.name, "direction", Values.Vector(direction), "depth", distance); return false; }
                if (direction.y < .52992f)
                {
                    // A landing can penetrate a tread lip before the next solver
                    // step. Its shortest escape is sideways, even though a clear
                    // resting pose is immediately above. Permit only shallow
                    // ground contacts whose native horizontal resolution retains
                    // a checked landing and clear supported connector. Include
                    // the same contact skin used by the independent body sweep.
                    Vector3 resolved = position + direction * (distance + .01f), landing;
                    Collider landingGround; RaycastHit landingHit;
                    if (direction.y < -.01f || distance > nativeRadius * .25f + Skin ||
                        support.y < position.y || support.y - position.y > StepHeight ||
                        !Stand(resolved, out landing, out landingGround, ContactRadius, out landingHit) ||
                        !Segment(support, landing, ContactRadius, false))
                    { if (TraceEnabled) Settlement = Json.Object("rejected", "lateral_penetration", "collider", collider.name, "direction", Values.Vector(direction), "depth", distance); return false; }
                }
                contact = true;
                if (contacts != null) contacts.Add(Json.Object("collider", collider.GetInstanceID(), "depth", distance, "direction", Values.Vector(direction)));
            }
            if (contact && TraceEnabled) Settlement = Json.Object("reason", "native_penetration", "contacts", contacts);
            return contact;
        }
        private bool StepSweep(Vector3 from, Vector3 to, float clearance)
        {
            if (Mathf.Abs(from.y - to.y) < .025f) return Sweep(from, to, clearance);
            // Up, across, down: sweep the body over a native-height step, retaining
            // wall and overhead clearance. Never ignore a ground collider wholesale.
            float top = Mathf.Max(from.y, to.y);
            Vector3 raisedFrom = new Vector3(from.x, top, from.z), raisedTo = new Vector3(to.x, top, to.z);
            return Sweep(from, raisedFrom, clearance) && Sweep(raisedFrom, raisedTo, clearance) && Sweep(raisedTo, to, clearance);
        }
        private float ContactRadius { get { return Mathf.Max(.01f, nativeRadius - .02f); } }
        private bool Sweep(Vector3 from, Vector3 to, float clearance)
        {
            Vector3 delta = to - from, low, high; float length = delta.magnitude;
            if (length < .0001f) return true;
            Capsule(from, out low, out high, clearance);
            int count = Physics.CapsuleCastNonAlloc(low, high, clearance, delta / length, hits, length, ~0, QueryTriggerInteraction.Collide); Queries++;
            if (count == hits.Length) { Overflow = true; return false; }
            for (int i = 0; i < count; i++) if (BlocksWalkingBody(hits[i].collider, Mathf.Min(from.y, to.y), Mathf.Max(from.y, to.y), low.y - clearance + Mathf.Min(0, delta.y), high.y + clearance + Mathf.Max(0, delta.y))) return Fail("body_sweep", from, hits[i].collider);
            return true;
        }
        private void TrackSurface(Collider ground, Vector3 point, ref IMovingSurface current, ref IMovingSurface previous)
        {
            IMovingSurface next = MovingSurface(ground);
            if (next == null) { current = previous = null; return; }
            if (next == current) return;
            previous = current;
            // Native travelators retain their entering belt through a corner.
            // For a connector starting under the chef, preserve that live history.
            if (current == null && ground == chef.GroundCollider && Vector3.Distance(point, Position) < .1f &&
                chef.SurfaceMovable != null && previousSurface != null)
                previous = previousSurface.GetValue(chef.SurfaceMovable) as IMovingSurface;
            current = next;
        }
        public Vector3 Flow(Vector3 at, Collider ground, IMovingSurface previous = null)
        {
            Vector3 drift = Vector3.zero;
            IMovingSurface surface = MovingSurface(ground);
            if (surface != null) drift += surface.CalculateVelocityAtPoint(at, previous);
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
        private IMovingSurface MovingSurface(Collider ground)
        {
            if (ground == null) return null;
            int id = ground.GetInstanceID(); IMovingSurface surface;
            if (!surfaces.TryGetValue(id, out surface))
            {
                foreach (var c in ground.GetComponents<MonoBehaviour>()) if (c is IMovingSurface) { surface = (IMovingSurface)c; break; }
                surfaces[id] = surface;
            }
            return surface;
        }
        public float Speed(Collider ground)
        {
            int id = ground == null ? 0 : ground.GetInstanceID(); float speed;
            if (speeds.TryGetValue(id, out speed)) return speed;
            PlayerPhysicsSurface surface = null;
            if (ground != null && !properties.TryGetValue(id, out surface))
            { surface = ground.GetComponent<PlayerPhysicsSurface>(); properties[id] = surface; }
            var p = surface == null ? null : surface.Properties;
            speed = p != null && (p.UseOverrideGravityNormal || p.Slidiness > .01f) ? 0 : walkingSpeed * (p == null ? 1 : p.SpeedMultiplier);
            // Re-read on every physics refresh, not every directed edge. No dynamic
            // speed or surface velocity is retained across updates.
            speeds[id] = speed; return speed;
        }
        private bool CanWalkDirection(Vector3 from, Vector3 to, Collider ground, Vector3 at, IMovingSurface previous = null)
        {
            if ((to - from).sqrMagnitude < .0001f) return true;
            float speed = Speed(ground);
            IMovingSurface surface = MovingSurface(ground);
            if (surface is Travelator)
            {
                // Treat a floor conveyor as directed navigation. Even when the
                // chef can just overpower it, an upstream distance shortcut can
                // take most of the action timeout and snag at native corners.
                // Use native world velocity, including borders and entry history;
                // sideways exits remain legal when the flow solver can steer them.
                Vector3 velocity = surface.CalculateVelocityAtPoint(at, previous);
                Vector3 direction = to - from; direction.y = 0; velocity.y = 0;
                if (Vector3.Dot(direction.normalized, velocity) < -.01f)
                {
                    Fail("conveyor_opposes_walking", at, ground);
                    if (TraceEnabled) failureSupport = Json.Object("surfaceVelocity", Values.Vector(velocity), "direction", Values.Vector(direction));
                    return false;
                }
            }
            if (winds.Count == 0 && surface == null) return speed > .1f;
            Vector3 drift = Flow(at, ground, previous); float x, z;
            return WalkingSteering.Direction(to.x - from.x, to.z - from.z, drift.x, drift.z, speed, out x, out z);
        }
        private IGridLocation Grid(Collider target)
        {
            IGridLocation grid = null;
            if (useGridSelection && !grids.TryGetValue(target.GetInstanceID(), out grid))
            {
                for (Transform t = target.transform; t != null && grid == null; t = t.parent)
                    foreach (MonoBehaviour component in t.GetComponents<MonoBehaviour>())
                        if (component is IGridLocation) { grid = (IGridLocation)component; break; }
                grids[target.GetInstanceID()] = grid;
            }
            return grid;
        }
        public bool Sight(Vector3 foot, Collider target)
        {
            if ((interactMask & (1 << target.gameObject.layer)) == 0) return false;
            IGridLocation grid = Grid(target);
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
            // A low loose ingredient is picked up from the floor, not viewed
            // from counter height. The old high ray could fail its vertical /
            // spherical range limit even when a native pickup was in reach.
            // Keep obstacle testing at the item's height and retain the normal
            // height/range checks for raised objects and grid stations.
            if (grid == null && target.GetComponentInParent<PhysicalAttachment>() != null &&
                target.bounds.max.y >= foot.y && target.bounds.max.y < foot.y + .5f)
                origin.y = Mathf.Max(foot.y + .1f, target.bounds.max.y);
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
