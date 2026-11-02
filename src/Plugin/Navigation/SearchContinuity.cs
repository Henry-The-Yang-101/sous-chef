using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        private struct GroundProof { public Collider Ground; public float Speed; public IMovingSurface Surface; }
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
        public string ContinuityFailure { get; private set; }
        private bool CannotContinue(string reason) { ContinuityFailure = reason; return false; }

        private void ForgetSearch()
        { searchRemembered = false; searchGrounds.Clear(); searchWinds.Clear(); searchConveyors.Clear(); searchPreviousSurface = null; geometryVersion++; }

        // Search progress is main-thread state, separate from the geometry cache.
        // Keep exact values, not a lossy hash or a time-based freshness guess.
        public bool RememberSearch()
        {
            searchRemembered = false; searchGrounds.Clear(); searchWinds.Clear();
            // Native conveyors have an explicit continuity proof. Arbitrary
            // moving surfaces and portal readiness retain fresh searches.
            if (portals.Count > 0 || Overflow) return CannotContinue(portals.Count > 0 ? "portal_predicates" : "physics_query_overflow");
            foreach (var pair in speeds)
            {
                GeometryStamp stamp; Collider ground = null;
                if (pair.Key != 0)
                {
                    if (!stamps.TryGetValue(pair.Key, out stamp)) return CannotContinue("untracked_support");
                    ground = stamp.Collider;
                    IMovingSurface moving = MovingSurface(ground);
                    if (ground == null || (moving != null && !(moving is Travelator))) return CannotContinue("moving_or_missing_support");
                }
                searchGrounds.Add(new GroundProof { Ground = ground, Speed = pair.Value, Surface = MovingSurface(ground) });
            }
            if (!RememberConveyors()) return CannotContinue("untracked_conveyor_rules");
            foreach (var pair in winds)
            {
                WindVolume wind = pair.Value;
                if (wind == null) return CannotContinue("missing_wind");
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
            if (!searchRemembered) return CannotContinue(ContinuityFailure ?? "no_saved_search");
            if (geometryVersion != savedGeometryVersion) return CannotContinue("geometry_changed");
            if (Overflow || chef != searchChef || held != searchHeld ||
                chef.GroundCollider != searchGround ||
                !Position.Equals(searchPosition) || walkingSpeed != searchSpeed || radius != searchRadius ||
                nativeRadius != searchNativeRadius || height != searchHeight || pivotToFoot != searchPivot ||
                StepHeight != searchStep || Physics.queriesHitBackfaces != searchBackfaces ||
                portals.Count > 0 || winds.Count != searchWinds.Count) return CannotContinue("starting_state_or_movement_rules_changed");
            foreach (GroundProof proof in searchGrounds)
            {
                if (!System.Object.ReferenceEquals(proof.Ground, null) && proof.Ground == null) return CannotContinue("support_removed");
                IMovingSurface moving = MovingSurface(proof.Ground);
                if (!System.Object.ReferenceEquals(moving, proof.Surface) || Speed(proof.Ground) != proof.Speed) return CannotContinue("surface_rules_changed");
            }
            if (!ConveyorsUnchanged()) return CannotContinue("conveyor_rules_changed");
            foreach (WindProof proof in searchWinds)
            {
                WindVolume wind;
                if (proof.Wind == null || !winds.TryGetValue(proof.Key, out wind) || wind != proof.Wind ||
                    wind.isActiveAndEnabled != proof.Active || !wind.GetVelocity().Equals(proof.Velocity) ||
                    ((LayerMask)windFilter.GetValue(wind)).value != proof.Filter) return CannotContinue("wind_changed");
            }
            ContinuityFailure = null; return true;
        }
    }
}
