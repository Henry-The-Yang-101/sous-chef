using System.Collections.Generic;
using UnityEngine;
using SousChef.Core;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        private struct GroundProof { public Collider Ground; public float Speed; public IMovingSurface Surface; }
        private readonly List<GroundProof> searchGrounds = new List<GroundProof>();
        private long geometryVersion, savedGeometryVersion, sceneRevision, savedSceneRevision;
        private bool searchRemembered, searchBackfaces;
        private Vector3 searchPosition;
        private List<WalkEdge> currentStarts, savedStarts;
        public long StartProofRevalidations { get; private set; }
        private PlayerControls searchChef;
        private GameObject searchHeld;
        private Collider searchGround;
        private float searchSpeed, searchRadius, searchNativeRadius, searchHeight, searchPivot, searchStep;
        public string ContinuityFailure { get; private set; }
        private bool CannotContinue(string reason) { ContinuityFailure = reason; return false; }

        private void ForgetSearch()
        { searchRemembered = false; searchGrounds.Clear(); searchConveyors.Clear(); searchPreviousSurface = null; currentStarts = savedStarts = null; geometryVersion++; }

        // Search progress is main-thread state, separate from the geometry cache.
        // Keep exact values, not a lossy hash or a time-based freshness guess.
        public bool RememberSearch(bool retainGrounds = false)
        {
            searchRemembered = false; if (!retainGrounds) searchGrounds.Clear();
            if (geometryCacheEvicted) return CannotContinue("geometry_cache_evicted");
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
                bool known = false;
                foreach (GroundProof proof in searchGrounds) if (System.Object.ReferenceEquals(proof.Ground, ground)) { known = true; break; }
                if (!known) searchGrounds.Add(new GroundProof { Ground = ground, Speed = pair.Value, Surface = MovingSurface(ground) });
            }
            if (!RememberConveyors()) return CannotContinue("untracked_conveyor_rules");
            searchChef = chef; searchHeld = held; searchGround = chef.GroundCollider;
            searchPosition = Position; savedGeometryVersion = geometryVersion; savedSceneRevision = sceneRevision; savedStarts = currentStarts;
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
                walkingSpeed != searchSpeed || radius != searchRadius ||
                nativeRadius != searchNativeRadius || height != searchHeight ||
                StepHeight != searchStep || Physics.queriesHitBackfaces != searchBackfaces ||
                portals.Count > 0) return CannotContinue("starting_state_or_movement_rules_changed");
            foreach (GroundProof proof in searchGrounds)
            {
                if (!System.Object.ReferenceEquals(proof.Ground, null) && proof.Ground == null) return CannotContinue("support_removed");
                IMovingSurface moving = MovingSurface(proof.Ground);
                if (!System.Object.ReferenceEquals(moving, proof.Surface) || Speed(proof.Ground) != proof.Speed) return CannotContinue("surface_rules_changed");
            }
            if (!ConveyorsUnchanged()) return CannotContinue("conveyor_rules_changed");
            if (!Position.Equals(searchPosition) || pivotToFoot != searchPivot)
            {
                // Standing chefs can oscillate vertically by float ULPs. Prove
                // equivalence through fresh native connectors and EXACT graph
                // costs, rather than accepting a positional tolerance.
                Vector3 now = Position;
                if (now.x != searchPosition.x || now.z != searchPosition.z || savedStarts == null)
                    return CannotContinue("starting_position_changed");
                foreach (GroundProof proof in searchGrounds)
                    if (proof.Surface != null) return CannotContinue("moving_surface_start_changed");
                float rangeSquared = (Range - 2) * (Range - 2);
                // Both admitted and rejected cached neighbours matter: admitting
                // an old out-of-range edge could improve an already settled cost.
                foreach (Probe probe in probes.Values)
                    if (((probe.Point - now).sqrMagnitude > rangeSquared) != ((probe.Point - searchPosition).sqrMagnitude > rangeSquared))
                        return CannotContinue("search_range_changed");
            }
            // Start connectors span up to two cells in each axis. Check their
            // exact ordered costs even when a change missed all lattice edges.
            if (sceneRevision != savedSceneRevision || !Position.Equals(searchPosition) || pivotToFoot != searchPivot)
            {
                if (savedStarts == null) return CannotContinue("no_saved_starts");
                List<WalkEdge> previous = savedStarts;
                Starts(); StartProofRevalidations++;
                if (Overflow || currentStarts.Count != previous.Count) return CannotContinue("starting_connectors_changed");
                for (int i = 0; i < previous.Count; i++)
                    if (!currentStarts[i].Cell.Equals(previous[i].Cell) || currentStarts[i].Distance != previous[i].Distance)
                        return CannotContinue("starting_connectors_changed");
            }
            ContinuityFailure = null; return true;
        }
    }
}
