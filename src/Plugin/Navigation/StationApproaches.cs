using System;
using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        // At most one fixed-station connector per cached lattice probe.
        private sealed class StationConnector
        {
            public long Revision;
            public Vector3 Guess, Stance;
            public Collider Ground;
            public float NativeRadius;
            public bool Backfaces;
        }

        public bool Approach(Vector3 foot, Collider target, out Vector3 stance, int surfaceHistory = 0)
        {
            return Approach(foot, target, out stance, surfaceHistory, null);
        }

        public bool CellApproach(WalkCell cell, Collider target, out Vector3 stance)
        {
            Probe probe = Sample(cell);
            return Approach(probe.Point, target, out stance, cell.SurfaceHistory, probe);
        }

        private bool Approach(Vector3 foot, Collider target, out Vector3 stance, int surfaceHistory, Probe probe)
        {
            stance = foot;
            if (target == null || (interactMask & (1 << target.gameObject.layer)) == 0) return false;
            IGridLocation grid = Grid(target);
            if (grid != null)
            {
                GridManager manager = grid.AccessGridManager;
                var component = grid as Component;
                if (manager == null || component == null || !manager.isActiveAndEnabled) return false;
                GridIndex at = manager.GetUnclampedGridLocationFromPos(foot + Vector3.up * pivotToFoot), destination = grid.GridIndex;
                if (at.Y != destination.Y || Math.Abs(at.X - destination.X) + Math.Abs(at.Z - destination.Z) != 1) return false;
                // Centre the chef on the counter's usable side. Track the actual
                // station transform, including a grid moving/rotating with a cart.
                Vector3 offset = manager.GetPosFromGridLocation(at) - manager.GetPosFromGridLocation(destination);
                stance = component.transform.position + offset;
            }
            else
            {
                Bounds bounds = target.bounds;
                Vector3 outward = foot - bounds.center; outward.y = 0;
                if (outward.sqrMagnitude < .001f) return false;
                outward.Normalize();
                Vector3 extent = bounds.extents;
                float reach = Mathf.Abs(outward.x) * extent.x + Mathf.Abs(outward.z) * extent.z;
                stance = bounds.center + outward * (reach + radius + .08f);
            }
            stance.y = foot.y;
            // Only attach a short, checked connector to the Dijkstra lattice.
            if ((stance - foot).sqrMagnitude > Spacing * Spacing * .75f) return false;
            // The connector starts outside the live chef's contact skin and ends
            // at the exact current grid stance. Selection and sight remain live.
            bool cacheable = probe != null && grid != null && winds.Count == 0 && exitPortal == null &&
                permittedPortalTrigger == null && Vector3.Distance(foot, Position) >= .02f;
            Vector3 guess = stance;
            StationConnector saved = cacheable ? probe.Connector : null;
            if (saved != null && saved.Revision == sceneRevision && saved.Guess.Equals(guess) &&
                saved.NativeRadius == nativeRadius && saved.Backfaces == Physics.queriesHitBackfaces &&
                saved.Ground != null && MovingSurface(saved.Ground) == null && Speed(saved.Ground) > .1f)
            {
                stance = saved.Stance;
                return Sight(stance, target);
            }
            Vector3 supported; Collider support;
            if (!Stand(stance, out supported, out support) || Mathf.Abs(supported.y - foot.y) > .22f) return false;
            stance = supported;
            // Alignment uses neutral walking and requires settled native motion.
            // A running belt cannot supply that stance: choose another reachable
            // side instead of repeatedly braking, drifting and walking upstream.
            IMovingSurface surface = MovingSurface(support), previous;
            surfaceHistories.TryGetValue(surfaceHistory, out previous);
            if (surface is Travelator && surface.CalculateVelocityAtPoint(stance, previous).magnitude > .2f)
                return Fail("conveyor_alignment_drift", stance, support);
            if (!Sight(stance, target)) return false;
            if (!cacheable || surface != null) return Segment(foot, stance, ref surfaceHistory);

            // Only remember a success on a single static support. Wind, belts,
            // mixed supports and contact connectors keep their native checks.
            var samples = new List<GroundSample>();
            if (!Segment(foot, stance, radius, true, samples, initialHistory: surfaceHistory)) return false;
            foreach (GroundSample sample in samples)
                if (sample.Ground != support) return true;
            probe.Connector = new StationConnector {
                Revision = sceneRevision, Guess = guess, Stance = stance, Ground = support,
                NativeRadius = nativeRadius, Backfaces = Physics.queriesHitBackfaces
            };
            return true;
        }
    }
}
