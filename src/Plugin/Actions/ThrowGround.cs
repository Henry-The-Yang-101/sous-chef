using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class ThrowPlanner
    {
        private bool ground;
        private Vector3 groundPoint;
        private int groundMask;
        private Collider groundSupport;
        private struct GroundSpot { public Vector3 Point; public Collider Support; public float Priority; }
        private readonly List<GroundSpot> groundSpots = new List<GroundSpot>();
        private bool GroundAt(Vector3 point, out RaycastHit contact)
        {
            contact = new RaycastHit();
            int count = Physics.RaycastNonAlloc(point + Vector3.up * .25f, Vector3.down, hits, .5f, groundMask, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return false;
            float nearest = Single.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.isTrigger || c.GetComponentInParent<PlayerControls>() != null || c.GetComponentInParent<PhysicalAttachment>() != null) continue;
                if (hits[i].distance < nearest) { nearest = hits[i].distance; contact = hits[i]; }
            }
            return nearest != Single.MaxValue && contact.normal.y > .9f && !DropHandoff.GroundHazard(contact.collider) && Math.Abs(contact.point.y - point.y) < .08f;
        }
        private Collider receivingSupport, motionSupport;
        private Vector3 groundLocalPoint, surfacePosition, previousPosition, surfaceScale, previousScale;
        private Quaternion surfaceRotation, previousRotation;
        private float surfaceTime, surfaceDelta;
        private static Vector3 Scaled(Vector3 point, Vector3 scale)
        { return new Vector3(point.x * scale.x, point.y * scale.y, point.z * scale.z); }
        private void ObserveGroundMotion()
        {
            float now = Time.realtimeSinceStartup;
            if (motionSupport != receivingSupport)
            { motionSupport = receivingSupport; surfaceTime = now; surfaceDelta = 0; }
            else if (now > surfaceTime)
            {
                previousPosition = surfacePosition; previousRotation = surfaceRotation; previousScale = surfaceScale;
                surfaceDelta = now - surfaceTime; surfaceTime = now;
            }
            surfacePosition = receivingSupport.transform.position; surfaceRotation = receivingSupport.transform.rotation;
            surfaceScale = receivingSupport.transform.lossyScale;
        }
        private Vector3 GroundVelocity()
        {
            ObserveGroundMotion();
            if (surfaceDelta <= 0) return Vector3.zero;
            Vector3 previous = previousPosition + previousRotation * Scaled(groundLocalPoint, previousScale);
            return (receivingSupport.transform.TransformPoint(groundLocalPoint) - previous) / surfaceDelta;
        }
        private void AddGround(Vector3 point, Vector3 human)
        {
            Vector3 difference = point - human; difference.y = 0;
            groundSpots.Add(new GroundSpot { Point = receivingSupport.transform.InverseTransformPoint(point),
                Support = receivingSupport, Priority = HandoffPreference.SpacingCost(difference.magnitude, groundTileSize) });
        }
        private float groundTileSize;
        private string PrepareGround()
        {
            groundSpots.Clear();
            if (player == chef || player.m_bRespawning || !player.gameObject.activeInHierarchy) return "player_unavailable";
            groundTileSize = HandoffPreference.TileSize(player);
            var cast = player.GetComponent<GroundCast>(); var capsule = player.GetComponent<CapsuleCollider>();
            if (cast == null || capsule == null) return "unsupported_ground_receiver";
            groundMask = ActionObservations.Read<LayerMask>(cast, "m_landscapeMask").value;
            Vector3 foot = player.transform.position; foot.y = capsule.bounds.min.y;
            RaycastHit below;
            if (!GroundAt(foot, out below)) return "no_nearby_ground";
            receivingSupport = player.GroundCollider ?? below.collider;
            if (receivingSupport == null || !receivingSupport.enabled || DropHandoff.GroundHazard(receivingSupport)) return "no_nearby_ground";
            ObserveGroundMotion();
            Vector3 toward = chef.transform.position - foot; toward.y = 0;
            if (toward.sqrMagnitude < .001f) toward = player.transform.forward;
            toward.Normalize();
            // Seed the preferred three-tile ring, then cover the receiving support. Distance is a
            // preference, never a cutoff. These are seeds; live native footprint,
            // obstacle and flight proofs are still required before selecting one.
            foreach (float radius in new[] { HandoffPreference.PreferredTiles * groundTileSize, groundTileSize })
                for (int n = 0; n < 8; n++)
                {
                    float angle = n * Mathf.PI / 4;
                    AddGround(foot + new Vector3(toward.x * Mathf.Cos(angle) - toward.z * Mathf.Sin(angle), 0,
                        toward.x * Mathf.Sin(angle) + toward.z * Mathf.Cos(angle)) * radius, foot);
                }
            var box = receivingSupport as BoxCollider;
            Vector3 center, size; bool localBox = box != null && Mathf.Abs(box.transform.up.y) > .9999f;
            if (localBox) { center = box.center; size = box.size; }
            else { center = receivingSupport.bounds.center; size = receivingSupport.bounds.size; }
            Vector3 scale = localBox ? receivingSupport.transform.lossyScale : Vector3.one;
            int nx = Math.Max(1, Mathf.CeilToInt(Mathf.Abs(size.x * scale.x) / .8f));
            int nz = Math.Max(1, Mathf.CeilToInt(Mathf.Abs(size.z * scale.z) / .8f));
            float stride = (float)Math.Sqrt(Math.Max(1, (float)nx * nz / 512));
            nx = Mathf.CeilToInt(nx / stride); nz = Mathf.CeilToInt(nz / stride);
            float localY = localBox ? receivingSupport.transform.InverseTransformPoint(foot).y : foot.y;
            for (int x = 0; x < nx; x++) for (int z = 0; z < nz; z++)
            {
                Vector3 point = new Vector3(center.x + ((x + .5f) / nx - .5f) * size.x, localY,
                    center.z + ((z + .5f) / nz - .5f) * size.z);
                AddGround(localBox ? receivingSupport.transform.TransformPoint(point) : point, foot);
            }
            groundSpots.Sort(delegate(GroundSpot a, GroundSpot b) { return a.Priority.CompareTo(b.Priority); });
            // Preserve a useful current-pose probe without eagerly certifying the
            // whole raft on the game thread. Find continues the remaining seeds.
            UseGround(groundSpots[0]);
            for (int n = 0; n < Math.Min(16, groundSpots.Count); n++)
                if (UseGround(groundSpots[n]) && GroundSupported(receiving.center, -toward, 0)) break;
            return null;
        }
        private bool RefreshGround() { return RefreshGround(chef.transform.forward); }
        private bool RefreshGround(Vector3 direction)
        {
            if (receivingSupport == null || !receivingSupport.enabled || !receivingSupport.gameObject.activeInHierarchy) return false;
            Vector3 point = receivingSupport.transform.TransformPoint(groundLocalPoint); RaycastHit contact;
            if (!GroundAt(point, out contact)) return false;
            groundPoint = contact.point; groundSupport = contact.collider;
            receiving = GroundReceiving(direction); return true;
        }
        private bool UseGround(GroundSpot spot)
        { receivingSupport = spot.Support; groundLocalPoint = spot.Point; return RefreshGround(); }
        // The cargo lands oriented along its planned throw, not along the
        // chef's facing while it walks or turns to aim.
        public bool RestoreGround(ThrowPlan plan)
        {
            if (!ground) return true;
            if (plan.GroundSupport != null)
            {
                if (receivingSupport != plan.GroundSupport) return false;
                groundLocalPoint = plan.GroundLocalPoint;
                return RefreshGround(plan.Direction) && GroundSupported(receiving.center, plan.Direction, 0);
            }
            RaycastHit contact;
            if (!GroundAt(player.transform.position + plan.GroundOffset, out contact)) return false;
            groundLocalPoint = receivingSupport.transform.InverseTransformPoint(contact.point);
            return RefreshGround(plan.Direction) && GroundSupported(receiving.center, plan.Direction, 0);
        }
        private Bounds GroundReceiving(Vector3 direction)
        {
            Bounds body = itemBody.BoundsAt(Vector3.zero, direction);
            Vector3 center = groundPoint - body.center; center.y = groundPoint.y - body.min.y;
            return new Bounds(center, new Vector3(.5f, .12f, .5f));
        }
        private bool GroundSupported(Vector3 arrival, Vector3 direction, float uncertainty)
        {
            Bounds body = itemBody.BoundsAt(arrival, direction);
            if (!GroundFootprintSupported(body, uncertainty)) return false;
            int count = itemBody.Overlap(arrival, direction, overlaps, ~0, .025f, uncertainty);
            if (count == overlaps.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider c = overlaps[i];
                if (c == null || c.transform.IsChildOf(item.transform) || c.transform.IsChildOf(chef.transform)) continue;
                if (DropHandoff.GroundHazard(c)) { lastObstruction = "ground_hazard:" + c.gameObject.name; return Reject(lastObstruction); }
                if (!c.isTrigger && (mask & (1 << c.gameObject.layer)) != 0 &&
                    !((groundMask & (1 << c.gameObject.layer)) != 0 && c.bounds.max.y <= body.min.y + .08f))
                { lastObstruction = "ground_collision:" + c.gameObject.name; return Reject(lastObstruction); }
            }
            return true;
        }
        private bool GroundFootprintSupported(Bounds body, float uncertainty)
        {
            // Check the entire landing footprint, including release uncertainty;
            // a point over the edge of a raft is not a supported delivery.
            float x = body.extents.x + uncertainty + .025f, z = body.extents.z + uncertainty + .025f;
            for (int n = 0; n < 5; n++)
            {
                Vector3 point = new Vector3(body.center.x + (n == 0 ? 0 : (n % 2 == 0 ? x : -x)), body.min.y,
                    body.center.z + (n == 0 ? 0 : (n <= 2 ? z : -z)));
                RaycastHit contact;
                if (!GroundAt(point, out contact)) { lastObstruction = "ground_support:" + (contact.collider == null ? "missing" : contact.collider.gameObject.name); return Reject(lastObstruction); }
                if (contact.collider != receivingSupport) return Reject("different_receiving_floor");
            }
            return true;
        }
        private bool GroundRunoutSupported(Vector3 arrival, Vector3 direction, Vector3 velocity, float uncertainty)
        {
            // Reserve support for a short post-impact slide in the observed
            // touchdown direction. This is an edge margin, not another flight
            // simulation: native friction/contact still determine the receipt.
            velocity.y = 0;
            for (int i = 1; i <= 3; i++)
                if (!GroundFootprintSupported(itemBody.BoundsAt(arrival + velocity * (.35f * i / 3), direction), uncertainty)) return false;
            return true;
        }
    }
}
