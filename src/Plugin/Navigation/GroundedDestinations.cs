using System;
using System.Collections.Generic;
using System.Diagnostics;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class NavigationActions
    {
        // A separate observer must never refresh the executor's physics/search.
        // References survive geometry motion and are revoked only with the kitchen.
        private readonly WalkPhysics mapPhysics = new WalkPhysics();
        private sealed class StandingReference
        {
            public string Id, SupportId;
            public Collider Ground;
            public Transform Anchor;
            public Vector3 Local, Position;
            public bool Valid, Platform;
            public float Observed;
        }
        private readonly Dictionary<string, StandingReference> standing = new Dictionary<string, StandingReference>();
        private readonly Dictionary<string, string> standingKeys = new Dictionary<string, string>();
        private readonly List<StandingReference> scanPoints = new List<StandingReference>();
        private readonly List<object> mapEdges = new List<object>();
        private readonly Dictionary<long,List<StandingReference>> edgeBuckets = new Dictionary<long,List<StandingReference>>();
        private readonly List<StandingReference> edgeNeighbours = new List<StandingReference>();
        private int neighbourIndex;
        private readonly List<Collider> scanGrounds = new List<Collider>();
        private readonly Dictionary<string, StandingReference> platformPoints = new Dictionary<string, StandingReference>();
        private sealed class MotionObservation { public Vector3 Position; public Matrix4x4 Matrix; public float Time; }
        private readonly Dictionary<string, MotionObservation> observedMotion = new Dictionary<string, MotionObservation>();
        private string mapEpoch = Guid.NewGuid().ToString("N"), mapError = "not_observed";
        private float mapUntil, mapRefresh = -1, scanCompleted = -1;
        private int groundIndex, seedX, seedZ, probeIndex, edgeIndex, scanGeneration;
        private int mapFrame=-1;
        private bool seeding, mapLimited;
        private double mapMilliseconds;
        private const int MaxStanding = 4096, MaxSeedsPerGround = 4096;
        private const float MapSpacing = .8f;
        private string movementReference, movementWaiting;
        public string MovementWaiting { get { return movementWaiting; } }

        private void ResetGroundedMap()
        {
            mapEpoch = Guid.NewGuid().ToString("N"); mapPhysics.Reset();
            standing.Clear(); standingKeys.Clear(); scanPoints.Clear(); scanGrounds.Clear(); platformPoints.Clear(); observedMotion.Clear(); mapEdges.Clear();
            edgeBuckets.Clear(); edgeNeighbours.Clear(); neighbourIndex=0;
            mapUntil = 0; mapRefresh = scanCompleted = -1; seeding = false; mapError = "not_observed";
            mapFrame=-1;
        }

        // DynamicLandscapeParenting uses this exact native ownership interface.
        // Neither common velocity nor a common scene root proves shared support.
        internal static Transform SupportOwner(Collider ground, GameObject actor, out bool platform)
        {
            platform = false;
            if (ground == null) return null;
            for (Transform t = ground.transform; t != null; t = t.parent)
                foreach (MonoBehaviour behaviour in t.GetComponents<MonoBehaviour>())
                {
                    var parentable = behaviour as IParentable;
                    if (parentable == null) continue;
                    Transform attach = parentable.GetAttachPoint(actor);
                    if (attach != null) { platform = true; return attach; }
                }
            return ground.transform;
        }
        private static string SupportId(Transform anchor) { return "support_" + anchor.GetInstanceID(); }
        private static bool Live(Collider ground)
        { return ground != null && ground.enabled && ground.gameObject.activeInHierarchy; }

        public void WarmGroundedMap(PlayerControls player)
        {
            if (player == null || Time.realtimeSinceStartup > mapUntil || mapFrame==Time.frameCount) return;
            mapFrame=Time.frameCount;
            var watch = Stopwatch.StartNew();
            try
            {
                if (mapRefresh < 0 || Time.realtimeSinceStartup - mapRefresh >= .25f)
                {
                    mapError = mapPhysics.Refresh(player); mapRefresh = Time.realtimeSinceStartup;
                    if (mapError != null) return;
                }
                if (!seeding && probeIndex >= scanPoints.Count && edgeIndex >= scanPoints.Count)
                {
                    if (scanCompleted >= 0 && Time.realtimeSinceStartup - scanCompleted < .5f) return;
                    scanGrounds.Clear();
                    foreach (Collider c in mapPhysics.Colliders) if (mapPhysics.DiscoveryGround(c)) scanGrounds.Add(c);
                    scanGrounds.Sort((a,b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                    groundIndex = seedX = seedZ = 0; seeding = true; mapLimited = false;
                    mapEdges.Clear(); scanGeneration++;
                }
                int operations = 0;
                while (watch.Elapsed.TotalMilliseconds < 2 && operations++ < 48)
                {
                    if (seeding)
                    {
                        if (groundIndex >= scanGrounds.Count)
                        {
                            seeding = false; scanPoints.Clear(); scanPoints.AddRange(standing.Values);
                            scanPoints.Sort((a,b) => String.CompareOrdinal(a.Id,b.Id)); probeIndex = edgeIndex = 0;
                            edgeBuckets.Clear(); edgeNeighbours.Clear(); neighbourIndex=0;
                            continue;
                        }
                        Collider c = scanGrounds[groundIndex];
                        if (!Live(c)) { groundIndex++; seedX = seedZ = 0; continue; }
                        Vector3 low, high; mapPhysics.DiscoveryBounds(c, out low, out high);
                        int nx = Math.Max(1, Mathf.CeilToInt((high.x-low.x)/MapSpacing)), nz = Math.Max(1, Mathf.CeilToInt((high.z-low.z)/MapSpacing));
                        if ((long)nx*nz > MaxSeedsPerGround) { mapLimited = true; groundIndex++; seedX = seedZ = 0; continue; }
                        Vector3 seed = new Vector3(low.x+(seedX+.5f)*(high.x-low.x)/nx, high.y, low.z+(seedZ+.5f)*(high.z-low.z)/nz);
                        string key = c.GetInstanceID()+":"+seedX+":"+seedZ+":"+nx+":"+nz;
                        if (!standingKeys.ContainsKey(key) && standing.Count < MaxStanding)
                        {
                            Vector3 point; Collider ground;
                            if (mapPhysics.DiscoveryStand(c, seed, out point, out ground))
                            {
                                bool platform; Transform anchor = SupportOwner(ground, player.gameObject, out platform);
                                var entry = new StandingReference { Id = "d"+(++nextId), Ground = ground, Anchor = anchor,
                                    Local = anchor.InverseTransformPoint(point), Position = point, SupportId = SupportId(anchor), Platform = platform,
                                    Valid = true, Observed = Time.realtimeSinceStartup };
                                standing[entry.Id] = entry; standingKeys[key] = entry.Id;
                            }
                        }
                        if (standing.Count >= MaxStanding) mapLimited = true;
                        if (++seedZ >= nz) { seedZ = 0; if (++seedX >= nx) { seedX = 0; groundIndex++; } }
                    }
                    else if (probeIndex < scanPoints.Count)
                    {
                        StandingReference p = scanPoints[probeIndex++];
                        p.Valid = ValidateStanding(p, mapPhysics); p.Observed = Time.realtimeSinceStartup;
                        if (p.Valid)
                        {
                            long key=EdgeBucket(p.Position); List<StandingReference> bucket;
                            if(!edgeBuckets.TryGetValue(key,out bucket)) { bucket=new List<StandingReference>(); edgeBuckets[key]=bucket; }
                            bucket.Add(p);
                        }
                    }
                    else if (edgeIndex < scanPoints.Count)
                    {
                        StandingReference a = scanPoints[edgeIndex];
                        if (!a.Valid) { edgeIndex++; continue; }
                        // Sparse observed edges are evidence of connections, never
                        // evidence of disconnection. Check one edge per budgeted
                        // operation; stacked decks cannot create an unbounded slice.
                        if (edgeNeighbours.Count==0)
                        {
                            int x=Mathf.FloorToInt(a.Position.x/(MapSpacing*1.5f)), z=Mathf.FloorToInt(a.Position.z/(MapSpacing*1.5f));
                            for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++)
                            {
                                List<StandingReference> bucket;
                                if(edgeBuckets.TryGetValue(((long)(x+dx)<<32)^(uint)(z+dz),out bucket))
                                    foreach(StandingReference b in bucket)
                                        if(String.CompareOrdinal(a.Id,b.Id)<0 && Vector3.Distance(a.Position,b.Position)<=MapSpacing*1.45f)
                                            edgeNeighbours.Add(b);
                            }
                        }
                        if(neighbourIndex<edgeNeighbours.Count)
                        {
                            StandingReference b=edgeNeighbours[neighbourIndex++];
                            if (mapPhysics.Segment(a.Position,b.Position) && mapPhysics.Segment(b.Position,a.Position))
                                mapEdges.Add(Json.Object("from",a.Id,"to",b.Id,"direction","both","observedAt",Time.realtimeSinceStartup));
                        }
                        if(neighbourIndex>=edgeNeighbours.Count) { edgeIndex++; edgeNeighbours.Clear(); neighbourIndex=0; }
                    }
                    else { scanCompleted = Time.realtimeSinceStartup; break; }
                }
            }
            finally { mapMilliseconds = watch.Elapsed.TotalMilliseconds; }
        }

        private static long EdgeBucket(Vector3 p)
        { return ((long)Mathf.FloorToInt(p.x/(MapSpacing*1.5f))<<32) ^ (uint)Mathf.FloorToInt(p.z/(MapSpacing*1.5f)); }

        private void ObserveMotion(string key, Transform anchor, Json.Record value)
        {
            MotionObservation previous; observedMotion.TryGetValue(key,out previous);
            float elapsed=previous==null ? 0 : Time.realtimeSinceStartup-previous.Time;
            value["motion"]=previous==null ? "unknown" : previous.Matrix!=anchor.localToWorldMatrix ? "observed_moving" : "observed_stationary";
            value["velocity"]=previous==null ? null : Values.Vector(elapsed>.001f ? (anchor.position-previous.Position)/elapsed : Vector3.zero);
            value["observedAt"]=Time.realtimeSinceStartup;
            observedMotion[key]=new MotionObservation { Position=anchor.position, Matrix=anchor.localToWorldMatrix, Time=Time.realtimeSinceStartup };
        }

        private static bool ValidateStanding(StandingReference p, WalkPhysics check)
        {
            if (!Live(p.Ground) || p.Anchor == null || !p.Anchor.gameObject.activeInHierarchy) return false;
            p.Position = p.Anchor.TransformPoint(p.Local);
            Vector3 point; Collider ground;
            if (!check.StationaryStand(p.Position, out point, out ground) || Vector3.Distance(point,p.Position) > .04f) return false;
            bool platform; Transform owner = SupportOwner(ground, null, out platform);
            return owner == p.Anchor;
        }

        public object GroundedMap(PlayerControls player)
        {
            mapUntil = Time.realtimeSinceStartup + 3;
            WarmGroundedMap(player);
            var points = new List<object>(); var platforms = new List<object>(); var targets = new List<object>(); var supports = new List<object>();
            var grouped = new Dictionary<string,List<StandingReference>>();
            foreach (StandingReference p in standing.Values)
            {
                if (p.Anchor != null) p.Position = p.Anchor.TransformPoint(p.Local);
                points.Add(Json.Object("id",p.Id,"binding","floor","position",Values.Vector(p.Position),"supportId",p.SupportId,
                    "supportLocalPosition",Values.Vector(p.Local),"platform",p.Platform,"standing",p.Valid ? "verified" : "blocked",
                    "observedAt",p.Observed,"routeStatus","unknown"));
                if (p.Platform && p.Valid && p.Anchor != null)
                {
                    List<StandingReference> group;
                    if (!grouped.TryGetValue(p.SupportId,out group)) { group=new List<StandingReference>(); grouped[p.SupportId]=group; }
                    group.Add(p);
                }
            }
            foreach (var group in grouped)
            {
                Vector3 centre=Vector3.zero; foreach(StandingReference p in group.Value) centre+=p.Position;
                centre/=group.Value.Count;
                StandingReference nearest=group.Value[0];
                foreach(StandingReference p in group.Value)
                    if(Vector3.Distance(p.Position,centre)<Vector3.Distance(nearest.Position,centre)) nearest=p;
                if (!platformPoints.ContainsKey(group.Key))
                    platformPoints[group.Key]=new StandingReference { Id="d"+(++nextId), Ground=nearest.Ground, Anchor=nearest.Anchor,
                        Local=nearest.Local, Position=nearest.Position, SupportId=nearest.SupportId, Platform=true, Valid=true, Observed=nearest.Observed };
                Transform anchor=nearest.Anchor;
                var occupants=new List<object>(); var stations=new List<object>();
                foreach(PlayerControls chefOnMap in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
                {
                    bool owned;
                    if(chefOnMap.PlayerIDProvider!=null && SupportOwner(chefOnMap.GroundCollider,chefOnMap.gameObject,out owned)==anchor)
                        occupants.Add((int)chefOnMap.PlayerIDProvider.GetID()+1);
                }
                foreach(Destination station in identities.Values)
                    if(station.Object!=null && station.Object.transform.IsChildOf(anchor)) stations.Add(station.Id);
                var supportValue=Json.Object("id",group.Key,"label",anchor.gameObject.name,"position",Values.Vector(centre),
                    "occupants",occupants,"stations",stations,"evidence","native_IParentable; observed transform delta; no arrival forecast");
                ObserveMotion(group.Key,anchor,supportValue); supports.Add(supportValue);
            }
            foreach (StandingReference p in platformPoints.Values)
                if (p.Anchor != null) platforms.Add(Json.Object("id",p.Id,"binding","platform","label",p.Anchor.gameObject.name,
                    "supportId",p.SupportId,"position",Values.Vector(p.Anchor.TransformPoint(p.Local)),
                    "supportLocalPosition",Values.Vector(p.Local),
                    "standing","requires_revalidation","routeStatus","unknown","ownershipEvidence","native_IParentable"));
            foreach (Destination d in identities.Values)
            {
                if (d.Object == null || !d.Object.activeInHierarchy) continue;
                var value = (Json.Record)Identity(d); value["binding"] = "station";
                ObserveMotion(d.Id,d.Object.transform,value);
                foreach(var group in grouped)
                    if(d.Object.transform.IsChildOf(group.Value[0].Anchor)) { value["supportId"]=group.Key; break; }
                value["standing"] = "requires_approach_check"; value["routeStatus"] = "unknown";
                if (available.ContainsKey(d.Id) && Time.realtimeSinceStartup-lastPlan < .2f)
                { value["routeStatus"] = "verified"; value["routeDistance"] = d.Distance; }
                targets.Add(value);
            }
            Camera camera = Camera.main;
            Vector3 right = camera == null ? Vector3.zero : camera.transform.right;
            Vector3 up = camera == null ? Vector3.zero : camera.transform.up;
            right.y = up.y = 0;
            bool oriented = camera != null && right.magnitude > .1f && up.magnitude > .1f;
            return Json.Object("version",1,"epoch",mapEpoch,"observedAt",Time.realtimeSinceStartup,
                "orientation",Json.Object("known",oriented,"source",oriented ? "game_camera" : "unavailable",
                    "right",Values.Vector(right.normalized),"up",Values.Vector(up.normalized)),
                "actor",Json.Object("playerId",2,"position",Values.Vector(player.transform.position)),
                "points",points,"platforms",platforms,"supports",supports,"stations",targets,"walkingEdges",mapEdges.ToArray(),
                "coverage",Json.Object("complete",false,"reason",mapError ?? (mapLimited ? "discovery_limit" : "sampled_geometry"),
                    "samplingComplete",!seeding && probeIndex>=scanPoints.Count && edgeIndex>=scanPoints.Count,
                    "pointCount",standing.Count,"generation",scanGeneration,"lastSweepAt",scanCompleted,
                    "sliceMilliseconds",mapMilliseconds,"connectivity","observed_bidirectional_edges_only; missing paths are unknown"));
        }

        public string StartGrounded(PlayerControls player, int owner, string reference)
        {
            if (Action.State != "idle") return "action_in_progress";
            StandingReference p; standing.TryGetValue(reference, out p);
            if (p == null) foreach (StandingReference platform in platformPoints.Values) if (platform.Id == reference) { p=platform; break; }
            Destination chosen = null;
            if (p != null)
            {
                if (!Live(p.Ground) || p.Anchor == null) return "destination_binding_stale";
                chosen = new Destination { Id=reference, Object=p.Ground.gameObject, Kind=p.Platform ? "platform" : "floor",
                    IsPoint=true, StrictPointArrival=true, PointSupport=p.Ground, PointAnchor=p.Anchor,
                    LocalApproach=p.Local, Approach=p.Anchor.TransformPoint(p.Local), MovementOnly=true };
            }
            else foreach (Destination d in identities.Values)
                if (d.Id == reference && d.Object != null && d.Object.activeInHierarchy) { chosen=d; break; }
            if (chosen == null) return "destination_binding_stale";
            chef=player; target=chosen; movementReference=reference; movementWaiting=null;
            if (!Action.Start(owner,reference,true)) return "action_in_progress";
            started=Time.realtimeSinceStartup; lastPlan=-1; route=null; fullCatalogueRequested=false;
            ResetWalkingProgress(); stallReroutes=0; arrivalDetails=null; arrival.Reset(); facingSince=-1;
            haveMotion=motionSampleValid=false; settlingSince=-1; teleportsCompleted=0; executionTrace.Clear();
            // Accept identity first; route eligibility is refreshed in Tick. A
            // closed passage is a neutral, cancellable wait, not a substitute.
            move(Vector3.zero,50); movementWaiting="checking_route"; return null;
        }

        private bool WaitForGroundedRoute(string reason)
        {
            if (movementReference == null) return false;
            ResetPlayerYield();
            movementWaiting=reason ?? "route_unknown"; route=null; arrival.Reset(); facingSince=-1;
            haveMotion=motionSampleValid=false; ResetWalkingProgress(); move(Vector3.zero,50);
            return true;
        }
    }
}
