using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        private struct GroundProof { public Collider Ground; public float Speed; }
        private struct WindProof { public WindVolume Wind; public Vector3 Velocity; public int Key, Filter; public bool Active; }
        private readonly List<GroundProof> searchGrounds = new List<GroundProof>();
        private readonly List<WindProof> searchWinds = new List<WindProof>();
        private long geometryVersion, savedGeometryVersion;
        private bool searchRemembered, searchBackfaces;
        private Vector3 searchPosition;
        private PlayerControls searchChef;
        private GameObject searchHeld;
        private Collider searchGround;
        private float searchSpeed, searchRadius, searchNativeRadius, searchHeight, searchPivot, searchStep;

        private void ForgetSearch()
        { searchRemembered = false; searchGrounds.Clear(); searchWinds.Clear(); geometryVersion++; }

        // Search progress is main-thread state, separate from the geometry cache.
        // Keep exact values, not a lossy hash or a time-based freshness guess.
        public bool RememberSearch()
        {
            searchRemembered = false; searchGrounds.Clear(); searchWinds.Clear();
            // Arbitrary IMovingSurface implementations depend on location and
            // entry history; portals also have live readiness predicates. Retain
            // the existing fresh-search path until those can be versioned safely.
            if (portals.Count > 0 || Overflow) return false;
            foreach (var pair in speeds)
            {
                GeometryStamp stamp; Collider ground = null;
                if (pair.Key != 0)
                {
                    if (!stamps.TryGetValue(pair.Key, out stamp)) return false;
                    ground = stamp.Collider;
                    if (ground == null || MovingSurface(ground) != null) return false;
                }
                searchGrounds.Add(new GroundProof { Ground = ground, Speed = pair.Value });
            }
            foreach (var pair in winds)
            {
                WindVolume wind = pair.Value;
                if (wind == null) return false;
                searchWinds.Add(new WindProof { Key = pair.Key, Wind = wind, Velocity = wind.GetVelocity(),
                    Filter = ((LayerMask)windFilter.GetValue(wind)).value, Active = wind.isActiveAndEnabled });
            }
            searchChef = chef; searchHeld = held; searchGround = chef.GroundCollider;
            searchPosition = Position; savedGeometryVersion = geometryVersion;
            searchSpeed = walkingSpeed; searchRadius = radius; searchNativeRadius = nativeRadius;
            searchHeight = height; searchPivot = pivotToFoot; searchStep = StepHeight;
            searchBackfaces = Physics.queriesHitBackfaces;
            searchRemembered = true; return true;
        }

        // Called only AFTER Refresh has reconciled geometry and cleared its
        // per-refresh speed cache. Newly observed field values must match the
        // ones used by every settled edge before Dijkstra may continue.
        public bool CanContinueSearch()
        {
            if (!searchRemembered || Overflow || chef != searchChef || held != searchHeld ||
                chef.GroundCollider != searchGround || geometryVersion != savedGeometryVersion ||
                !Position.Equals(searchPosition) || walkingSpeed != searchSpeed || radius != searchRadius ||
                nativeRadius != searchNativeRadius || height != searchHeight || pivotToFoot != searchPivot ||
                StepHeight != searchStep || Physics.queriesHitBackfaces != searchBackfaces ||
                portals.Count > 0 || winds.Count != searchWinds.Count) return false;
            foreach (GroundProof proof in searchGrounds)
            {
                if (!System.Object.ReferenceEquals(proof.Ground, null) && proof.Ground == null) return false;
                if (MovingSurface(proof.Ground) != null || Speed(proof.Ground) != proof.Speed) return false;
            }
            foreach (WindProof proof in searchWinds)
            {
                WindVolume wind;
                if (proof.Wind == null || !winds.TryGetValue(proof.Key, out wind) || wind != proof.Wind ||
                    wind.isActiveAndEnabled != proof.Active || !wind.GetVelocity().Equals(proof.Velocity) ||
                    ((LayerMask)windFilter.GetValue(wind)).value != proof.Filter) return false;
            }
            return true;
        }
    }
}
