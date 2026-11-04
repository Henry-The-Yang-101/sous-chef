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
        private struct GroundSpot { public Vector3 Point; public Collider Support; }
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
        private string PrepareGround()
        {
            groundSpots.Clear();
            if (player == chef || player.m_bRespawning || !player.gameObject.activeInHierarchy) return "player_unavailable";
            var cast = player.GetComponent<GroundCast>(); var capsule = player.GetComponent<CapsuleCollider>();
            if (cast == null || capsule == null) return "unsupported_ground_receiver";
            groundMask = ActionObservations.Read<LayerMask>(cast, "m_landscapeMask").value;
            Vector3 toward = chef.transform.position - player.transform.position; toward.y = 0;
            if (toward.sqrMagnitude < .001f) toward = player.transform.forward;
            toward.Normalize();
            Vector3 foot = player.transform.position; foot.y = capsule.bounds.min.y;
            // Land beside the human, outside their body. Neither free hands nor
            // facing the incoming food is required for a floor delivery.
            for (int n = 0; n < 8; n++)
            {
                float angle = (n % 2 == 0 ? n / 2 : -(n + 1) / 2) * Mathf.PI / 4;
                Vector3 offset = new Vector3(toward.x * Mathf.Cos(angle) - toward.z * Mathf.Sin(angle), 0,
                    toward.x * Mathf.Sin(angle) + toward.z * Mathf.Cos(angle)) * 1.25f;
                RaycastHit contact;
                if (!GroundAt(foot + offset, out contact)) continue;
                groundPoint = contact.point; groundSupport = contact.collider;
                receiving = GroundReceiving(-toward);
                if (GroundSupported(receiving.center, -toward, ArrivalCheck.PositionTolerance + .02f))
                    groundSpots.Add(new GroundSpot {Point = groundPoint, Support = groundSupport});
            }
            if (groundSpots.Count == 0) return "no_nearby_ground";
            UseGround(groundSpots[0]); return null;
        }
        private void UseGround(GroundSpot spot)
        { groundPoint = spot.Point; groundSupport = spot.Support; receiving = GroundReceiving(chef.transform.forward); }
        public bool RestoreGround(ThrowPlan plan)
        {
            if (!ground) return true;
            RaycastHit contact;
            if (!GroundAt(player.transform.position + plan.GroundOffset, out contact)) return false;
            UseGround(new GroundSpot {Point = contact.point, Support = contact.collider});
            return GroundSupported(receiving.center, chef.transform.forward, 0);
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
            // Check the entire landing footprint, including release uncertainty;
            // a point over the edge of a raft is not a supported delivery.
            float x = body.extents.x + uncertainty + .025f, z = body.extents.z + uncertainty + .025f;
            for (int n = 0; n < 5; n++)
            {
                Vector3 point = new Vector3(body.center.x + (n == 0 ? 0 : (n % 2 == 0 ? x : -x)), body.min.y,
                    body.center.z + (n == 0 ? 0 : (n <= 2 ? z : -z)));
                RaycastHit contact;
                if (!GroundAt(point, out contact)) { lastObstruction = "ground_support:" + (contact.collider == null ? "missing" : contact.collider.gameObject.name); return false; }
            }
            int count = itemBody.Overlap(arrival, direction, overlaps, ~0, .025f, uncertainty);
            if (count == overlaps.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider c = overlaps[i];
                if (c == null || c.transform.IsChildOf(item.transform) || c.transform.IsChildOf(chef.transform)) continue;
                if (DropHandoff.GroundHazard(c)) { lastObstruction = "ground_hazard:" + c.gameObject.name; return false; }
                if (!c.isTrigger && (mask & (1 << c.gameObject.layer)) != 0 &&
                    !((groundMask & (1 << c.gameObject.layer)) != 0 && c.bounds.max.y <= body.min.y + .08f))
                { lastObstruction = "ground_collision:" + c.gameObject.name; return false; }
            }
            return true;
        }
    }
}
